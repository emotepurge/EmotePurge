#!/usr/bin/env node
// Exports open SonarCloud findings (bugs, vulnerabilities, code smells) as a SARIF 2.1.0
// file, for upload to GitHub code scanning ("Security" tab). This script is READ-ONLY
// against both SonarCloud and GitHub: it makes no GitHub API calls at all. GitHub itself
// owns alert identity and lifecycle once a SARIF file is uploaded (via
// `github/codeql-action/upload-sarif` or `gh api .../code-scanning/sarifs`) — a finding
// missing from the next upload is automatically marked "Fixed", and a finding that
// reappears reactivates the same alert (matched by rule + location + fingerprint). No
// issue creation, no labels, no dedup bookkeeping needed here.
//
// NOTE: SonarCloud "Security Hotspots" are NOT included. Hotspots live under a separate
// API (`/api/hotspots/search`) with their own TO_REVIEW/REVIEWED workflow and are out of
// scope for this exporter — only `/api/issues/search` (bugs, vulnerabilities, code
// smells) is used.
//
// Required env: SONAR_PROJECT_KEY
// Optional env: SONAR_TOKEN (HTTP Basic, token as username, empty password),
//               SONAR_HOST_URL (default "https://sonarcloud.io"),
//               SONAR_REPORT_TASK_FILE (default ".sonarqube/out/.sonar/report-task.txt"),
//               SARIF_OUTPUT (default "sonar.sarif"),
//               SONAR_SKIP_CE_WAIT ("true"/"false", default "false" — testing/debug only,
//               see waitForComputeEngineTask below)

import { readFile, writeFile, appendFile, stat } from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";

// GitHub code scanning SARIF limits, as documented at
// https://docs.github.com/en/code-security/reference/code-scanning/sarif-files/troubleshoot-sarif-uploads/results-exceed-limit
// and https://docs.github.com/en/code-security/code-scanning/troubleshooting-sarif-uploads/file-too-large
// (checked live 2026-09-06). These are hard/soft limits GitHub enforces on upload, not
// numbers we invented — if GitHub changes them, update the comment + constants together.
const GITHUB_LIMITS = {
  maxResultsPerRunHard: 25000, // uploads above this are rejected
  maxResultsPerRunDisplayed: 5000, // only the top N (by severity) are shown/prioritized
  maxRulesPerRun: 25000,
  maxRunsPerFile: 20,
  // The docs describe 20 with "only the top 10 displayed", but GitHub's own upload warning says
  // otherwise: "had 15 tags which is more than our limit of 10. Only 10 tags were stored for that
  // rule, the additional ones were ignored." Observed behaviour wins — the surplus is dropped, not
  // merely hidden, so anything that must survive has to be inside the first ten.
  maxTagsPerRule: 10,
  maxFileSizeBytes: 10 * 1024 * 1024, // upload is rejected above this
};

const SONAR_ISSUES_API = "/api/issues/search";
const SONAR_RULES_API = "/api/rules/search";
const SONAR_CE_TASK_API = "/api/ce/task";
const RULE_CHUNK_SIZE = 60; // keeps rule_keys query strings comfortably short
const CE_POLL_INTERVAL_MS = 5000;
const CE_POLL_TIMEOUT_MS = 10 * 60 * 1000;

function readEnv() {
  const sonarProjectKey = process.env.SONAR_PROJECT_KEY;
  if (!sonarProjectKey) {
    throw new Error("Missing environment variable: SONAR_PROJECT_KEY");
  }
  const sonarHostUrl = (process.env.SONAR_HOST_URL || "https://sonarcloud.io").replace(/\/$/, "");
  const reportTaskFile = process.env.SONAR_REPORT_TASK_FILE || ".sonarqube/out/.sonar/report-task.txt";
  const sarifOutput = process.env.SARIF_OUTPUT || "sonar.sarif";
  const skipCeWait = String(process.env.SONAR_SKIP_CE_WAIT ?? "false").toLowerCase() === "true";
  return {
    sonarProjectKey,
    sonarToken: process.env.SONAR_TOKEN,
    sonarHostUrl,
    reportTaskFile,
    sarifOutput,
    skipCeWait,
  };
}

function authHeaders(token) {
  if (!token) return {};
  const credentials = Buffer.from(`${token}:`).toString("base64");
  return { Authorization: `Basic ${credentials}` };
}

async function httpJson(url, options, context) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const body = await response.text().catch(() => "<body not readable>");
    throw new Error(
      `HTTP error for ${context}: ${response.status} ${response.statusText}\n${url}\n${body}`,
    );
  }
  return response.json();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function parseCeTaskId(reportTaskContent) {
  const match = /^ceTaskId=(.+)$/m.exec(reportTaskContent);
  return match ? match[1].trim() : null;
}

// `dotnet-sonarscanner end` only uploads and returns immediately — the server-side
// evaluation (Compute Engine) keeps running asynchronously afterwards. Querying the
// issues API right away would return the PREVIOUS analysis's findings, silently stale.
// The `end` step leaves behind report-task.txt with a `ceTaskId=`/`ceTaskUrl=` pair;
// we read the id from there and poll the CE task API until it leaves PENDING/IN_PROGRESS.
async function waitForComputeEngineTask(env) {
  if (env.skipCeWait) {
    console.log(
      "SONAR_SKIP_CE_WAIT=true is set — skipping the Compute Engine wait loop. " +
        "Intended ONLY for tests/debugging: in a real CI run the scanner's `end` step must " +
        "have run beforehand and the analysis must be complete, otherwise a stale state would be exported.",
    );
    return;
  }

  const resolvedPath = path.isAbsolute(env.reportTaskFile)
    ? env.reportTaskFile
    : path.resolve(process.cwd(), env.reportTaskFile);

  let content;
  try {
    content = await readFile(resolvedPath, "utf8");
  } catch {
    throw new Error(
      `report-task.txt not found at ${resolvedPath}. The scanner's \`end\` step ` +
        `(e.g. \`dotnet-sonarscanner end\`) must have run successfully before this — otherwise there is no ` +
        `Compute Engine task to wait for, and an export without the wait loop would ` +
        `silently deliver the old analysis state. To test locally without a real scanner run, ` +
        `set SONAR_SKIP_CE_WAIT=true.`,
    );
  }

  const ceTaskId = parseCeTaskId(content);
  if (!ceTaskId) {
    throw new Error(`report-task.txt at ${resolvedPath} does not contain a "ceTaskId=" line.`);
  }

  console.log(`Waiting for Compute Engine task ${ceTaskId}...`);
  const headers = authHeaders(env.sonarToken);
  const deadline = Date.now() + CE_POLL_TIMEOUT_MS;
  for (;;) {
    const url = new URL(`${env.sonarHostUrl}${SONAR_CE_TASK_API}`);
    url.searchParams.set("id", ceTaskId);
    const data = await httpJson(url, { headers }, `querying Compute Engine task ${ceTaskId}`);
    const status = data.task?.status;
    if (status === "SUCCESS") {
      console.log(`Compute Engine task ${ceTaskId} completed (SUCCESS).`);
      return;
    }
    if (status === "FAILED" || status === "CANCELED") {
      throw new Error(`Compute Engine task ${ceTaskId} has status ${status} — analysis failed or was canceled.`);
    }
    if (status !== "PENDING" && status !== "IN_PROGRESS") {
      throw new Error(`Compute Engine task ${ceTaskId} has unexpected status "${status}".`);
    }
    if (Date.now() >= deadline) {
      throw new Error(
        `Timed out waiting for Compute Engine task ${ceTaskId} after ${CE_POLL_TIMEOUT_MS / 1000}s ` +
          `(last status: ${status}).`,
      );
    }
    await sleep(CE_POLL_INTERVAL_MS);
  }
}

// Fetches all open findings (resolved=false) from SonarCloud's issue search, paginating
// with ps=500 until paging.total is reached. Aborts loudly instead of silently truncating
// if SonarCloud's own 10,000-result search cap would be hit.
async function fetchAllSonarIssues(env) {
  const headers = authHeaders(env.sonarToken);
  const pageSize = 500;
  let page = 1;
  let total = null;
  const issues = [];
  for (;;) {
    const url = new URL(`${env.sonarHostUrl}${SONAR_ISSUES_API}`);
    url.searchParams.set("componentKeys", env.sonarProjectKey);
    url.searchParams.set("resolved", "false");
    url.searchParams.set("ps", String(pageSize));
    url.searchParams.set("p", String(page));
    const data = await httpJson(url, { headers }, `SonarCloud search (page ${page})`);
    total = data.paging?.total ?? data.total ?? 0;
    if (total > 10000) {
      throw new Error(
        `SonarCloud reports ${total} open findings — that is above the API cap of 10,000 results ` +
          `per search. Continuing to paginate would silently return incomplete results. Aborting.`,
      );
    }
    issues.push(...(data.issues ?? []));
    if (issues.length >= total || (data.issues ?? []).length === 0) break;
    page += 1;
  }
  if (issues.length !== total) {
    throw new Error(
      `Pagination inconsistent: ${issues.length} findings loaded, but SonarCloud reports total=${total}.`,
    );
  }
  return issues;
}

function chunk(array, size) {
  const chunks = [];
  for (let i = 0; i < array.length; i += size) {
    chunks.push(array.slice(i, i + size));
  }
  return chunks;
}

// Fetches rule metadata (name, description, tags) for the given rule keys, in chunks to
// keep query strings short. Returns a Map keyed by rule key; a rule missing from the
// response (network hiccup, retired rule, or — as observed live against the public
// SonarCloud API without a token — htmlDesc/mdDesc/descriptionSections simply not being
// returned even when requested) is handled by the caller via graceful degradation, not
// by throwing.
async function fetchRuleMetadata(env, ruleKeys) {
  const metadata = new Map();
  if (ruleKeys.length === 0) return metadata;

  // The rules API needs an organization, unlike issues/search. SonarCloud issues always
  // carry it; take it from the first finding we have.
  const organization = env.organization;
  const headers = authHeaders(env.sonarToken);

  for (const keyChunk of chunk(ruleKeys, RULE_CHUNK_SIZE)) {
    const url = new URL(`${env.sonarHostUrl}${SONAR_RULES_API}`);
    url.searchParams.set("organization", organization);
    url.searchParams.set("rule_keys", keyChunk.join(","));
    url.searchParams.set("ps", String(RULE_CHUNK_SIZE));
    url.searchParams.set("f", "name,htmlDesc,severity,sysTags");
    let data;
    try {
      data = await httpJson(url, { headers }, "loading rule metadata");
    } catch (error) {
      console.warn(
        `Warning: rule metadata for one block (${keyChunk.length} rules) could not be loaded ` +
          `(${error.message}). These rules degrade to their rule ID as name.`,
      );
      continue;
    }
    for (const rule of data.rules ?? []) {
      metadata.set(rule.key, rule);
    }
  }
  return metadata;
}

// Strips trailing spaces/tabs that immediately precede a newline. Written as a manual linear
// scan instead of `.replace(/[ \t]+\n/g, "\n")` (javascript:S8786): that regex is quadratic on
// input containing a long run of spaces/tabs that is NOT immediately followed by a newline — at
// every position within the run the engine greedily consumes to the end of the run, fails to
// find "\n", and backtracks one character at a time before giving up and advancing by one
// position, which is O(run length) work repeated at every position in the run. Measured live
// (scripts, not shipped in the export): 4k/16k/64k-char adversarial runs took ~11/177/2841 ms
// with the regex and ~0.1/0.2/0.3 ms with this scan — including the harder case of a long run
// followed by more non-whitespace text on the same line, where naive alternatives (e.g. an
// atomic-group emulation, or per-line `String#replace` with a `$`-anchored pattern) stay
// quadratic for the same reason. A trailing run with no following newline at all (the very end
// of the string, no final line break) is deliberately left untouched here — the final `.trim()`
// in htmlToPlainText below removes it either way, so behaviour is unchanged for every real input.
function stripTrailingSpaceBeforeNewlines(text) {
  let result = "";
  let index = 0;
  while (index < text.length) {
    const newlineIndex = text.indexOf("\n", index);
    const hasNewline = newlineIndex !== -1;
    const lineEnd = hasNewline ? newlineIndex : text.length;
    let contentEnd = lineEnd;
    while (contentEnd > index && (text[contentEnd - 1] === " " || text[contentEnd - 1] === "\t")) {
      contentEnd -= 1;
    }
    result += text.slice(index, contentEnd);
    if (hasNewline) result += "\n";
    index = lineEnd + 1;
  }
  return result;
}

function htmlToPlainText(html) {
  if (!html) return null;
  return stripTrailingSpaceBeforeNewlines(
    html
      .replace(/<br\s*\/?>/gi, "\n")
      .replace(/<\/(p|div|li|h[1-6])>/gi, "\n")
      // `[^<>]+` instead of `[^>]+` (javascript:S8786): the latter is quadratic on a long run of
      // unmatched "<" (e.g. mangled/partial markup) — greedy match to end of string, backtrack
      // one char at a time looking for ">", fail, advance one position, repeat. Excluding "<"
      // from the tag-content class too makes a "<" inside an unterminated run fail the whole
      // attempt immediately (no backtracking possible) instead of being swallowed as content, so
      // each position costs O(1) instead of O(remaining length). Measured live: a 64k-char run of
      // unmatched "<" took ~2.36s with the old pattern, ~0.3ms with this one, same output on
      // every well-formed-HTML input. This only differs from the original on malformed HTML that
      // has a literal (unescaped) "<" before a tag's closing ">" — not something SonarCloud's own
      // rule descriptions produce, since valid HTML must escape a stray "<" as "&lt;" already.
      .replace(/<[^<>]+>/g, "")
      .replaceAll("&amp;", "&")
      .replaceAll("&lt;", "<")
      .replaceAll("&gt;", ">")
      .replaceAll("&quot;", '"')
      .replaceAll("&#39;", "'"),
  )
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

function ruleLanguage(ruleKey) {
  const separatorIndex = ruleKey.indexOf(":");
  return separatorIndex === -1 ? ruleKey : ruleKey.slice(0, separatorIndex);
}

function ruleHelpUri(hostUrl, organization, ruleKey) {
  return `${hostUrl}/organizations/${encodeURIComponent(organization)}/rules?open=${encodeURIComponent(ruleKey)}&rule_key=${encodeURIComponent(ruleKey)}`;
}

// GitHub reads security-severity from the RULE descriptor
// (runs[].tool.driver.rules[].properties.security-severity), never from a result's own
// properties bag. Placing it on the result passes SARIF schema validation but is silently
// ignored, so the findings lose their High/Medium/Low classification. Sonar carries the
// severity per issue while SARIF carries it per rule, so a rule that produced several
// vulnerabilities is scored by its worst one.
function buildSecuritySeverityByRule(issues) {
  const byRule = new Map();
  for (const issue of issues) {
    if (issue.type !== "VULNERABILITY") continue;
    const score = Number(securitySeverityScore(issue.severity));
    const current = byRule.get(issue.rule);
    if (current === undefined || score > current) byRule.set(issue.rule, score);
  }
  return new Map([...byRule].map(([ruleKey, score]) => [ruleKey, score.toFixed(1)]));
}

// Builds the SARIF rule (reportingDescriptor) objects, in the same order the caller
// will use for ruleIndex. Missing metadata degrades to the rule key as name/description
// rather than failing the export.
function buildRuleEntries(ruleKeys, metadataByKey, hostUrl, organization, securitySeverityByRule) {
  return ruleKeys.map((ruleKey) => {
    const meta = metadataByKey.get(ruleKey);
    const name = meta?.name ?? ruleKey;
    const fullDescriptionText = htmlToPlainText(meta?.htmlDesc) ?? name;
    const tags = [...(meta?.sysTags ?? [])];
    if (meta?.type) tags.push(meta.type.toLowerCase().replace("_", "-"));

    const properties = { tags };
    const securitySeverity = securitySeverityByRule?.get(ruleKey);
    if (securitySeverity !== undefined) {
      properties["security-severity"] = securitySeverity;
      // GitHub only treats a scored rule as a security rule when it is tagged as one, and it keeps
      // only the first maxTagsPerRule tags. Appending would put the tag last, where a rule carrying
      // ten tags of its own drops it -- and the score would then be read as an ordinary quality
      // finding. Leading with it makes that impossible regardless of how many tags Sonar supplies.
      properties.tags = ["security", ...tags.filter((tag) => tag !== "security")];
    }
    properties.tags = properties.tags.slice(0, GITHUB_LIMITS.maxTagsPerRule);

    return {
      id: ruleKey,
      name,
      shortDescription: { text: name },
      fullDescription: { text: fullDescriptionText },
      helpUri: ruleHelpUri(hostUrl, organization, ruleKey),
      properties,
    };
  });
}

function severityToSarifLevel(severity) {
  switch (severity) {
    case "BLOCKER":
    case "CRITICAL":
      return "error";
    case "MAJOR":
      return "warning";
    case "MINOR":
    case "INFO":
      return "note";
    default:
      return "note";
  }
}

function securitySeverityScore(severity) {
  switch (severity) {
    case "BLOCKER":
      return "9.0";
    case "CRITICAL":
      return "7.5";
    case "MAJOR":
      return "5.5";
    case "MINOR":
      return "3.0";
    default:
      return "1.0";
  }
}

function stripProjectPrefix(component, projectKey) {
  const prefix = `${projectKey}:`;
  return component.startsWith(prefix) ? component.slice(prefix.length) : null;
}

function buildRegion(issue) {
  if (issue.textRange) {
    const region = {
      startLine: issue.textRange.startLine,
      endLine: issue.textRange.endLine,
    };
    if (typeof issue.textRange.startOffset === "number") {
      region.startColumn = issue.textRange.startOffset + 1; // Sonar offsets are 0-based, SARIF columns 1-based
    }
    if (typeof issue.textRange.endOffset === "number") {
      region.endColumn = issue.textRange.endOffset + 1;
    }
    return region;
  }
  if (typeof issue.line === "number") {
    return { startLine: issue.line };
  }
  return { startLine: 1 };
}

// Builds one SARIF result per finding. Returns null for findings that resolve to no
// file path at all (project-level findings) — the caller counts and skips those.
function buildResult(issue, projectKey, ruleIndexByKey) {
  const filePath = stripProjectPrefix(issue.component, projectKey);
  if (!filePath) return null;

  const result = {
    ruleId: issue.rule,
    ruleIndex: ruleIndexByKey.get(issue.rule),
    level: severityToSarifLevel(issue.severity),
    message: { text: issue.message },
    locations: [
      {
        physicalLocation: {
          artifactLocation: { uri: filePath },
          region: buildRegion(issue),
        },
      },
    ],
  };

  if (issue.hash) {
    result.partialFingerprints = { primaryLocationLineHash: issue.hash };
  }

  return result;
}

function groupCount(items, keyFn) {
  const counts = new Map();
  for (const item of items) {
    const key = keyFn(item);
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return counts;
}

function formatCountsTable(counts) {
  return [...counts.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([key, count]) => `  - ${key}: ${count}`)
    .join("\n");
}

function buildMarkdownSummary({ issues, results, skippedNoFile, sarifOutput, warnings }) {
  const rows = (counts) =>
    [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([key, count]) => `| ${key} | ${count} |`)
      .join("\n") || "| _(none)_ | 0 |";

  const byType = groupCount(issues, (issue) => issue.type);
  const bySeverity = groupCount(issues, (issue) => issue.severity);
  const byLanguage = groupCount(issues, (issue) => ruleLanguage(issue.rule));

  return [
    "## SonarCloud → SARIF Export",
    "",
    `- Findings total: **${issues.length}**`,
    `- SARIF results written: **${results.length}**`,
    `- Skipped (no file path): **${skippedNoFile}**`,
    `- Output file: \`${sarifOutput}\``,
    "",
    "### By type",
    "",
    "| Type | Count |",
    "| --- | --- |",
    rows(byType),
    "",
    "### By severity",
    "",
    "| Severity | Count |",
    "| --- | --- |",
    rows(bySeverity),
    "",
    "### By language",
    "",
    "| Language | Count |",
    "| --- | --- |",
    rows(byLanguage),
    "",
    ...(warnings.length > 0 ? ["### Warnings", "", ...warnings.map((w) => `- ${w}`), ""] : []),
  ].join("\n");
}

// Explicit comparator: the default sort is lexicographic, which is what these rule keys want,
// but naming it keeps the intent readable and satisfies javascript:S2871 — and, being a plain
// if/return chain rather than a nested ternary, javascript:S3358.
function compareLexicographically(a, b) {
  if (a < b) return -1;
  if (a > b) return 1;
  return 0;
}

async function main() {
  const env = readEnv();

  await waitForComputeEngineTask(env);

  console.log(`Loading open SonarCloud findings for project "${env.sonarProjectKey}"...`);
  const issues = await fetchAllSonarIssues(env);
  console.log(`${issues.length} open findings loaded.`);

  env.organization = issues[0]?.organization;

  const uniqueRuleKeys = [...new Set(issues.map((issue) => issue.rule))].sort(compareLexicographically);
  console.log(`Loading rule metadata for ${uniqueRuleKeys.length} unique rules...`);
  const ruleMetadata = uniqueRuleKeys.length > 0 ? await fetchRuleMetadata(env, uniqueRuleKeys) : new Map();
  const rulesMissingMetadata = uniqueRuleKeys.filter((key) => !ruleMetadata.has(key) || !ruleMetadata.get(key)?.name);
  if (rulesMissingMetadata.length > 0) {
    console.warn(
      `Warning: ${rulesMissingMetadata.length} rule(s) without a name from the rules API, degrading to rule ID: ` +
        rulesMissingMetadata.join(", "),
    );
  }

  const securitySeverityByRule = buildSecuritySeverityByRule(issues);
  const ruleEntries = buildRuleEntries(
    uniqueRuleKeys,
    ruleMetadata,
    env.sonarHostUrl,
    env.organization ?? "unknown",
    securitySeverityByRule,
  );
  const ruleIndexByKey = new Map(uniqueRuleKeys.map((key, index) => [key, index]));

  let skippedNoFile = 0;
  const results = [];
  for (const issue of issues) {
    const result = buildResult(issue, env.sonarProjectKey, ruleIndexByKey);
    if (result === null) {
      skippedNoFile += 1;
      continue;
    }
    results.push(result);
  }

  const warnings = [];
  if (ruleEntries.length > GITHUB_LIMITS.maxRulesPerRun) {
    warnings.push(
      `${ruleEntries.length} rules exceed GitHub's hard limit of ${GITHUB_LIMITS.maxRulesPerRun} rules per run.`,
    );
  }
  if (results.length > GITHUB_LIMITS.maxResultsPerRunHard) {
    warnings.push(
      `${results.length} results exceed GitHub's hard limit of ${GITHUB_LIMITS.maxResultsPerRunHard} results ` +
        `per run — the upload would be rejected by GitHub.`,
    );
  } else if (results.length > GITHUB_LIMITS.maxResultsPerRunDisplayed) {
    warnings.push(
      `${results.length} results exceed GitHub's soft limit of ${GITHUB_LIMITS.maxResultsPerRunDisplayed} — ` +
        `only the top ${GITHUB_LIMITS.maxResultsPerRunDisplayed} by severity will be shown as prioritized.`,
    );
  }
  for (const rule of ruleEntries) {
    if (rule.properties.tags.length > GITHUB_LIMITS.maxTagsPerRule) {
      warnings.push(
        `Rule ${rule.id} has ${rule.properties.tags.length} tags, GitHub only shows the first 10 of these ` +
          `(hard limit ${GITHUB_LIMITS.maxTagsPerRule}).`,
      );
    }
  }

  const sarif = {
    $schema: "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
    version: "2.1.0",
    runs: [
      {
        tool: {
          driver: {
            name: "SonarCloud",
            informationUri: `${env.sonarHostUrl}/dashboard?id=${encodeURIComponent(env.sonarProjectKey)}`,
            rules: ruleEntries,
          },
        },
        results,
      },
    ],
  };

  const sarifJson = JSON.stringify(sarif, null, 2);
  await writeFile(env.sarifOutput, sarifJson, "utf8");
  const fileStats = await stat(env.sarifOutput);
  console.log(`SARIF file written: ${env.sarifOutput} (${fileStats.size} bytes).`);

  if (fileStats.size > GITHUB_LIMITS.maxFileSizeBytes) {
    warnings.push(
      `File is ${fileStats.size} bytes, exceeding GitHub's 10 MB upload limit — the upload would be rejected.`,
    );
  } else if (fileStats.size > GITHUB_LIMITS.maxFileSizeBytes * 0.8) {
    warnings.push(
      `File is approaching GitHub's 10 MB upload limit at ${fileStats.size} bytes.`,
    );
  }

  const byType = groupCount(issues, (issue) => issue.type);
  const bySeverity = groupCount(issues, (issue) => issue.severity);
  const byLanguage = groupCount(issues, (issue) => ruleLanguage(issue.rule));

  console.log("\n=== Summary ===");
  console.log(`Findings total: ${issues.length}`);
  console.log(`SARIF results written: ${results.length}`);
  console.log(`Skipped (no file path): ${skippedNoFile}`);
  console.log("By type:");
  console.log(formatCountsTable(byType) || "  (none)");
  console.log("By severity:");
  console.log(formatCountsTable(bySeverity) || "  (none)");
  console.log("By language:");
  console.log(formatCountsTable(byLanguage) || "  (none)");
  console.log(`Output path: ${env.sarifOutput}`);
  if (warnings.length > 0) {
    console.log("\nWarnings:");
    for (const warning of warnings) console.log(`  - ${warning}`);
  }

  const summaryPath = process.env.GITHUB_STEP_SUMMARY;
  if (summaryPath) {
    await appendFile(
      summaryPath,
      buildMarkdownSummary({ issues, results, skippedNoFile, sarifOutput: env.sarifOutput, warnings }),
    );
    console.log(`\nSummary additionally written to GITHUB_STEP_SUMMARY (${summaryPath}).`);
  }
}

// `file://${process.argv[1]}` is NOT the canonical form of import.meta.url once the path
// contains spaces, non-ASCII characters, or (on Windows) backslashes/a drive letter — the
// comparison would then silently be false and the script would do nothing at all, with exit 0.
// pathToFileURL() builds the same URL Node itself used for import.meta.url.
const isDirectRun = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isDirectRun) {
  try {
    await main();
  } catch (error) {
    console.error(`Aborted with error: ${error.message}`);
    process.exitCode = 1;
  }
}

export {
  parseCeTaskId,
  htmlToPlainText,
  ruleLanguage,
  stripProjectPrefix,
  buildRegion,
  buildResult,
  severityToSarifLevel,
  securitySeverityScore,
  buildRuleEntries,
  buildSecuritySeverityByRule,
};
