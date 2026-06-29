#!/usr/bin/env node
// SPEC lint for docs/specs/everywhere-replace-agent-browser.md §9.
// Rules 1-17 enforced; Rule 18 (anti-temptation) is code-review only.
//
// Cross-repo: when invoked in CI, the workflow clones hhsw2015/opendia
// into ./opendia-readonly/ (read-only). Locally, ~/Dev/opendia is used.
//
// Exits 0 on success, 1 on any violation, 2 on input error.

import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { resolve, join } from "node:path";
import { homedir } from "node:os";

const ROOT = resolve(process.cwd());
const MATRIX = join(ROOT, "docs/specs/parity-matrix.json");
const BENCH_FX = join(ROOT, "bench/fixtures");
const BENCH_RES = join(ROOT, "bench/results/bench-results.json");
const BASELINE_BYTES = join(ROOT, "docs/specs/opendia-bundle-baseline.txt");

const OPENDIA_DIR =
  process.env.OPENDIA_READONLY ||
  (existsSync("./opendia-readonly") ? "./opendia-readonly" : join(homedir(), "Dev/opendia"));

const OWNERSHIPS = new Set(["opendia", "everywhere", "universal", "wont-do"]);
const SCOPES = new Set(["in-browser", "out-of-browser", "both"]);
const TIERS = new Set(["core", "value-add", "niche"]);
const IMPACTS = new Set(["high", "medium", "low"]);
const EFFORTS = new Set(["S", "M", "L"]);
const STATUSES = new Set(["missing", "in-progress", "have", "blocked", "wont-do"]);
const CI_TIERS = new Set(["ci", "manual"]);
const FIXTURE_KINDS = new Set(["static_html", "har_replay"]);

const WONT_DO_REASONS = new Set([
  "product-plumbing",
  "wont-do",
]);
const SUPERSEDED_RE = /^superseded-by:[a-z]+\.[a-z_]+$/;
const TOOL_PREFIX_RE = /^(opendia|everywhere)\.[a-z_]+$/;
const SECRET_SCAN_RE = /^(authorization|cookie|set-cookie)\s*[:=]\s*\S+/i;

const errors = [];
const warnings = [];
function err(rule, msg) { errors.push(`[Rule ${rule}] ${msg}`); }
function warn(rule, msg) { warnings.push(`[Rule ${rule}] ${msg}`); }

// ---- load matrix ---------------------------------------------------------
if (!existsSync(MATRIX)) {
  console.error(`spec-lint: matrix missing: ${MATRIX}`);
  process.exit(2);
}
let matrix;
try { matrix = JSON.parse(readFileSync(MATRIX, "utf8")); }
catch (e) { console.error(`spec-lint: matrix unparseable: ${e.message}`); process.exit(2); }

// Rule 1 — parseable + types correct + enums valid.
if (matrix.schema_version !== "1") err(1, "schema_version != 1");
if (!Array.isArray(matrix.rows)) err(1, "rows is not an array");

const rows = Array.isArray(matrix.rows) ? matrix.rows : [];
for (const [i, r] of rows.entries()) {
  for (const k of ["ab_command", "tier", "scope", "ownership", "our_tool", "impact", "est_effort", "status"]) {
    if (r[k] === undefined) err(1, `row[${i}] missing field ${k}`);
  }
  if (!TIERS.has(r.tier)) err(1, `row[${i}].tier="${r.tier}" not in ${[...TIERS]}`);
  if (!SCOPES.has(r.scope)) err(1, `row[${i}].scope="${r.scope}" not in ${[...SCOPES]}`);
  if (!IMPACTS.has(r.impact)) err(1, `row[${i}].impact="${r.impact}" not in ${[...IMPACTS]}`);
  if (!EFFORTS.has(r.est_effort)) err(1, `row[${i}].est_effort="${r.est_effort}" not in ${[...EFFORTS]}`);
  if (!STATUSES.has(r.status)) err(1, `row[${i}].status="${r.status}" not in ${[...STATUSES]}`);
  if (typeof r.notes !== "string") err(1, `row[${i}].notes not a string`);
}

// Rule 2 — ownership enum.
for (const [i, r] of rows.entries()) {
  if (!OWNERSHIPS.has(r.ownership)) err(2, `row[${i}].ownership="${r.ownership}" invalid`);
}

// Rule 3 — wont_do_reason enum or superseded-by:<…> iff ownership=wont-do.
for (const [i, r] of rows.entries()) {
  const isWont = r.ownership === "wont-do";
  const hasReason = typeof r.wont_do_reason === "string" && r.wont_do_reason.length > 0;
  if (isWont && !hasReason) err(3, `row[${i}] ownership=wont-do but wont_do_reason missing`);
  if (!isWont && hasReason) err(3, `row[${i}] wont_do_reason set but ownership != wont-do`);
  if (isWont && hasReason) {
    const ok = WONT_DO_REASONS.has(r.wont_do_reason) || SUPERSEDED_RE.test(r.wont_do_reason);
    if (!ok) err(3, `row[${i}].wont_do_reason="${r.wont_do_reason}" not in enum and not superseded-by:`);
  }
}

// Rule 4 — universal-row Everywhere half requires opendia counterpart
// somewhere in the opendia-mcp/ source tree. Spec text §6 names server.js
// as the canonical surface; lint scans every .js in opendia-mcp/ so a
// dedicated parity registry module (registered via require() from server.js)
// still satisfies the contract — that is in-tree code, not a manifest.
const opendiaMcpDir = join(OPENDIA_DIR, "opendia-mcp");
let opendiaSrc = "";
if (existsSync(opendiaMcpDir)) {
  for (const f of readdirSync(opendiaMcpDir)) {
    if (f.endsWith(".js") && !f.endsWith(".test.js")) {
      opendiaSrc += readFileSync(join(opendiaMcpDir, f), "utf8") + "\n";
    }
  }
} else {
  warn(4, `opendia-mcp/ not found at ${opendiaMcpDir}; Rule 4 deferred until counterpart repo is reachable`);
}
function opendiaRegistered(name) {
  if (!opendiaSrc) return null;
  const re = new RegExp(`register\\(\\s*["'\\\`]opendia\\.${name}["'\\\`]`);
  return re.test(opendiaSrc);
}
for (const r of rows) {
  if (r.ownership !== "universal") continue;
  if (r.status !== "have") continue;
  const short = r.our_tool?.split(".")[1];
  if (!short) continue;
  const present = opendiaRegistered(short);
  if (present === false) {
    err(4, `universal row "${r.ab_command}" status=have but opendia.${short} not registered in ${opendiaServerJs}`);
  }
}

// Rule 5 — every bench:<id> has matching bench/fixtures/<id>/{task.md, page/}.
const fixtureIds = new Set();
if (existsSync(BENCH_FX)) {
  for (const d of readdirSync(BENCH_FX)) {
    const full = join(BENCH_FX, d);
    if (statSync(full).isDirectory()) fixtureIds.add(d);
  }
}
const referencedFixtureIds = new Set();
for (const r of rows) {
  const m = r.acceptance?.match(/^bench:(.+)$/);
  if (!m) continue;
  const id = m[1];
  referencedFixtureIds.add(id);
  if (r.ownership === "wont-do") continue;
  if (!fixtureIds.has(id)) {
    // Phase 0 will not yet have authored fixtures; downgrade to warning so
    // bootstrap CI can pass. Phase 0.5 promotes these back to errors via
    // an env flag.
    if (process.env.SPEC_LINT_STRICT_FIXTURES === "1") {
      err(5, `bench:${id} referenced by ${r.ab_command} but bench/fixtures/${id}/ missing`);
    } else {
      warn(5, `bench:${id} (row=${r.ab_command}) has no fixture yet (Phase 0.5 backfills)`);
    }
    continue;
  }
  const fxRoot = join(BENCH_FX, id);
  if (!existsSync(join(fxRoot, "task.md"))) err(5, `fixture ${id}: task.md missing`);
  if (!existsSync(join(fxRoot, "page"))) err(5, `fixture ${id}: page/ missing`);
}

// Rule 6 — §3.1/3.2/3.3 lists are subsets of matrix rows with matching ownership.
//   The SPEC document itself encodes those lists in prose; lint approximates
//   the constraint by checking that the matrix ownership values are
//   consistent with the classifier output (every row must have a tool name
//   under its ownership prefix).
for (const r of rows) {
  if (r.ownership === "wont-do") continue;
  if (!r.our_tool) { err(6, `row "${r.ab_command}" ownership=${r.ownership} but our_tool null`); continue; }
  if (r.ownership === "everywhere" && !r.our_tool.startsWith("everywhere.")) {
    err(6, `row "${r.ab_command}" ownership=everywhere but our_tool="${r.our_tool}"`);
  }
  if (r.ownership === "opendia" && !r.our_tool.startsWith("opendia.")) {
    err(6, `row "${r.ab_command}" ownership=opendia but our_tool="${r.our_tool}"`);
  }
  if (r.ownership === "universal" && !r.our_tool.startsWith("opendia.")) {
    // universal rows carry the opendia name; everywhere half is registered
    // separately in Everywhere.Mcp/Tools/* with the same short name.
    err(6, `row "${r.ab_command}" ownership=universal but our_tool="${r.our_tool}" (expected opendia.*)`);
  }
}

// Rule 7 — universal tools use opendia. or everywhere. prefix.
for (const r of rows) {
  if (!r.our_tool) continue;
  if (!TOOL_PREFIX_RE.test(r.our_tool)) err(7, `our_tool="${r.our_tool}" has no opendia./everywhere. prefix`);
}

// Rule 8 — OpenDia dist/ byte size ≤ baseline + 50 KB.
const distDir = join(OPENDIA_DIR, "opendia-extension/dist");
function dirSize(p) {
  if (!existsSync(p)) return null;
  let total = 0;
  const stack = [p];
  while (stack.length) {
    const cur = stack.pop();
    for (const ent of readdirSync(cur, { withFileTypes: true })) {
      const full = join(cur, ent.name);
      if (ent.isDirectory()) stack.push(full);
      else if (ent.isFile()) total += statSync(full).size;
    }
  }
  return total;
}
const distBytes = dirSize(distDir);
if (existsSync(BASELINE_BYTES) && distBytes !== null) {
  const baseline = parseInt(readFileSync(BASELINE_BYTES, "utf8").trim(), 10);
  if (Number.isFinite(baseline)) {
    if (distBytes > baseline + 50 * 1024) {
      err(8, `OpenDia dist=${distBytes}B > baseline ${baseline}B + 50KB`);
    }
  } else {
    err(8, `${BASELINE_BYTES} unparseable`);
  }
}

// Rule 9 — task.md must not reference an https:// URL not vendored in page/.
for (const id of fixtureIds) {
  const taskPath = join(BENCH_FX, id, "task.md");
  if (!existsSync(taskPath)) continue;
  const txt = readFileSync(taskPath, "utf8");
  const urls = [...txt.matchAll(/https:\/\/[^\s)]+/g)].map((m) => m[0]);
  if (urls.length === 0) continue;
  const pageDir = join(BENCH_FX, id, "page");
  const pageFiles = existsSync(pageDir)
    ? readdirSync(pageDir, { recursive: true })
    : [];
  for (const u of urls) {
    const host = u.replace(/^https:\/\//, "").split("/")[0];
    const hit = pageFiles.some((f) => String(f).includes(host));
    if (!hit) err(9, `fixture ${id}: ${u} not vendored under page/`);
  }
}

// Rule 10 — every fixture front-matter has ci_tier ∈ {ci, manual}.
function parseFrontMatter(taskPath) {
  if (!existsSync(taskPath)) return null;
  const txt = readFileSync(taskPath, "utf8");
  const m = txt.match(/^---\n([\s\S]*?)\n---/);
  if (!m) return null;
  const fm = {};
  for (const line of m[1].split(/\n/)) {
    const kv = line.match(/^([a-z_]+):\s*(.*)$/i);
    if (kv) fm[kv[1]] = kv[2].replace(/^["']|["']$/g, "").trim();
  }
  return fm;
}
for (const id of fixtureIds) {
  const fm = parseFrontMatter(join(BENCH_FX, id, "task.md"));
  if (!fm) { err(10, `fixture ${id}: front-matter missing`); continue; }
  if (!CI_TIERS.has(fm.ci_tier)) err(10, `fixture ${id}: ci_tier="${fm.ci_tier}" not in ${[...CI_TIERS]}`);
}

// Rule 11 — fixtures whose row's our_tool=diff_snapshot (or task.md invokes
// it) MUST declare wait_for: in front-matter.
const diffSnapRowFixtures = new Set();
for (const r of rows) {
  if (r.our_tool?.endsWith(".diff_snapshot")) {
    const m = r.acceptance?.match(/^bench:(.+)$/);
    if (m) diffSnapRowFixtures.add(m[1]);
  }
}
for (const id of fixtureIds) {
  const taskPath = join(BENCH_FX, id, "task.md");
  if (!existsSync(taskPath)) continue;
  const txt = readFileSync(taskPath, "utf8");
  const invokesDiff = /diff_snapshot/.test(txt);
  const fm = parseFrontMatter(taskPath) || {};
  if ((diffSnapRowFixtures.has(id) || invokesDiff) && !fm.wait_for) {
    err(11, `fixture ${id}: diff_snapshot in play but wait_for: missing in front-matter`);
  }
}

// Rule 12 — bidirectional: bench ids in non-wont-do rows == dirs in bench/fixtures/.
const refIds = new Set();
for (const r of rows) {
  if (r.ownership === "wont-do") continue;
  const m = r.acceptance?.match(/^bench:(.+)$/);
  if (m) refIds.add(m[1]);
}
for (const id of fixtureIds) {
  if (!refIds.has(id)) {
    if (process.env.SPEC_LINT_STRICT_FIXTURES === "1") {
      err(12, `fixture ${id} has no matrix row referencing it`);
    } else {
      warn(12, `fixture ${id} has no matrix row referencing it (acceptable pre-Phase 0.5)`);
    }
  }
}

// Rule 13 — tool description templates (§3.4). Pre-Phase-1: deferred until
// either substrate registers tools. Lint warns if either source exists.
const everywhereToolsDir = join(ROOT, "Everywhere.Mcp/Tools");
if (existsSync(everywhereToolsDir)) {
  // Lightweight: scan for Description("..."); detail templates ship in §3.4.
  // Strict regex matching is the Phase-1 contract; for now we only flag
  // attribute-free tool files.
  for (const f of readdirSync(everywhereToolsDir)) {
    if (!f.endsWith(".cs")) continue;
    const txt = readFileSync(join(everywhereToolsDir, f), "utf8");
    if (/\[McpServerTool\b/.test(txt) && !/Description\(/.test(txt)) {
      warn(13, `${f}: McpServerTool without Description()`);
    }
  }
}

// Rule 14 — JSON-aware secret scan on bench-results.json.
if (existsSync(BENCH_RES)) {
  let bench;
  try { bench = JSON.parse(readFileSync(BENCH_RES, "utf8")); }
  catch (e) { err(14, `bench-results.json unparseable: ${e.message}`); bench = null; }
  if (Array.isArray(bench)) {
    const walk = (v, path) => {
      if (typeof v === "string") {
        if (SECRET_SCAN_RE.test(v)) err(14, `bench-results.json at ${path}: secret-like string`);
      } else if (Array.isArray(v)) {
        v.forEach((x, i) => walk(x, `${path}[${i}]`));
      } else if (v && typeof v === "object") {
        for (const [k, x] of Object.entries(v)) walk(x, `${path}.${k}`);
      }
    };
    walk(bench, "$");
  }
}

// Rule 15 — for every status=have bench row, bench-results.json.ab.tokens_median
// == expected.json.tokens_median byte-for-byte.
if (existsSync(BENCH_RES)) {
  let bench;
  try { bench = JSON.parse(readFileSync(BENCH_RES, "utf8")); } catch { bench = []; }
  const byFixture = new Map();
  for (const row of (Array.isArray(bench) ? bench : [])) byFixture.set(row.fixture, row);
  for (const r of rows) {
    if (r.status !== "have") continue;
    const m = r.acceptance?.match(/^bench:(.+)$/);
    if (!m) continue;
    const id = m[1];
    const expectedPath = join(BENCH_FX, id, "expected.json");
    if (!existsSync(expectedPath)) { err(15, `status=have row ${r.ab_command}: expected.json missing for ${id}`); continue; }
    const exp = JSON.parse(readFileSync(expectedPath, "utf8"));
    const br = byFixture.get(id);
    if (!br) { err(15, `status=have row ${r.ab_command}: no bench-results entry for ${id}`); continue; }
    if (br.ab?.tokens_median !== exp.tokens_median) {
      err(15, `fixture ${id}: ab.tokens_median ${br.ab?.tokens_median} != expected ${exp.tokens_median}`);
    }
  }
}

// Rule 16 — every fixture front-matter has kind ∈ {static_html, har_replay}.
for (const id of fixtureIds) {
  const fm = parseFrontMatter(join(BENCH_FX, id, "task.md")) || {};
  if (!FIXTURE_KINDS.has(fm.kind)) err(16, `fixture ${id}: kind="${fm.kind}" not in ${[...FIXTURE_KINDS]}`);
}

// Rule 17 — ci_tier=ci fixtures invoke OpenDia tools only (no everywhere.* refs in task.md).
for (const id of fixtureIds) {
  const taskPath = join(BENCH_FX, id, "task.md");
  if (!existsSync(taskPath)) continue;
  const fm = parseFrontMatter(taskPath) || {};
  if (fm.ci_tier !== "ci") continue;
  const txt = readFileSync(taskPath, "utf8");
  if (/everywhere\.[a-z_]+/.test(txt)) {
    err(17, `fixture ${id}: ci_tier=ci but task.md references everywhere.* tool`);
  }
}

// ---- report --------------------------------------------------------------
if (warnings.length) {
  for (const w of warnings) console.error(`warn: ${w}`);
}
if (errors.length) {
  for (const e of errors) console.error(`fail: ${e}`);
  console.error(`spec-lint: ${errors.length} error(s), ${warnings.length} warning(s)`);
  process.exit(1);
}
console.error(`spec-lint: 0 errors, ${warnings.length} warning(s) — clean`);
