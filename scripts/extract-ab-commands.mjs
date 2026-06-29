#!/usr/bin/env node
// Extract agent-browser tool registrations from a locked sha of
// vercel-labs/agent-browser and emit docs/specs/parity-matrix.json.
// Algorithm: §5.1 + §6 step 3 of everywhere-replace-agent-browser.md.
//
// Inputs:
//   --src  path to ab clone (default /tmp/agent-browser)
//   --sha  expected sha     (default ed2e10598c9064aecfaeb7cf21b540684db4be2c)
//   --out  output path      (default docs/specs/parity-matrix.json)
// The script is idempotent: if --out exists, it merges, preserving fields
// editable by the SPEC loop (status, last_push_sha, last_bench_run, notes).

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { execSync } from "node:child_process";
import { resolve, join } from "node:path";

const AB_SHA = "ed2e10598c9064aecfaeb7cf21b540684db4be2c";

function parseArgv(av) {
  const out = {};
  for (let i = 0; i < av.length; i++) {
    const a = av[i];
    if (!a.startsWith("--")) continue;
    const eq = a.indexOf("=");
    if (eq !== -1) {
      out[a.slice(2, eq)] = a.slice(eq + 1);
    } else {
      const k = a.slice(2);
      const next = av[i + 1];
      if (next && !next.startsWith("--")) { out[k] = next; i++; }
      else out[k] = true;
    }
  }
  return out;
}
const argv = parseArgv(process.argv.slice(2));
const src = resolve(typeof argv.src === "string" ? argv.src : "/tmp/agent-browser");
const expectedSha = typeof argv.sha === "string" ? argv.sha : AB_SHA;
const outPath = resolve(typeof argv.out === "string" ? argv.out : "docs/specs/parity-matrix.json");

function die(msg) {
  console.error(`extract-ab-commands: ${msg}`);
  process.exit(1);
}

if (!existsSync(src)) die(`source not found: ${src}`);
let actualSha;
try {
  actualSha = execSync(`git -C ${src} rev-parse HEAD`, { encoding: "utf8" }).trim();
} catch (e) {
  die(`git rev-parse failed in ${src}: ${e.message}`);
}
if (actualSha !== expectedSha) {
  die(`sha mismatch: ${actualSha} != ${expectedSha} (locked)`);
}

const mcpRs = readFileSync(join(src, "cli/src/mcp.rs"), "utf8");

// Tool name set: union of all "agent_browser_*" string literals.
const nameSet = new Set();
for (const m of mcpRs.matchAll(/"(agent_browser_[a-z_]+)"/g)) {
  nameSet.add(m[1]);
}
// Drop names that only appear in negative-assertion tests
// (`!names.contains(&"agent_browser_..."`).
const negationRe = /!names\.contains\(\s*&"(agent_browser_[a-z_]+)"\s*\)/g;
for (const m of mcpRs.matchAll(negationRe)) nameSet.delete(m[1]);

const names = [...nameSet].sort();
// At locked sha ed2e105 the file embeds 152 distinct "agent_browser_*"
// string literals; one (`frame_list`) is asserted NOT registered, so the
// real surface is 151 tools. Hard-pin to that number so a refactor that
// silently flips the count fails the SPEC loop.
if (names.length !== 151) {
  die(`expected 151 registered ab tools, found ${names.length}`);
}

// Profile membership (§ matrix tier signal).
function profileSet(constName) {
  const re = new RegExp(`const ${constName}: &\\[&str\\] = &\\[([\\s\\S]+?)\\];`);
  const m = mcpRs.match(re);
  if (!m) return new Set();
  return new Set(
    [...m[1].matchAll(/TOOL_([A-Z_]+)/g)].map((x) => x[1]),
  );
}

// Build TOOL_CONST → "agent_browser_name" map.
const constMap = new Map();
for (const m of mcpRs.matchAll(/^const (TOOL_[A-Z_]+): &str = "(agent_browser_[a-z_]+)";/gm)) {
  constMap.set(m[1], m[2]);
}
function profileNames(constName) {
  const set = new Set();
  for (const tag of profileSet(constName)) {
    const name = constMap.get(`TOOL_${tag}`);
    if (name) set.add(name);
  }
  return set;
}
const corePf = profileNames("CORE_PROFILE_TOOLS");
const networkPf = profileNames("NETWORK_PROFILE_TOOLS");
const statePf = profileNames("STATE_PROFILE_TOOLS");
const debugPf = profileNames("DEBUG_PROFILE_TOOLS");
const tabsPf = profileNames("TABS_PROFILE_TOOLS");
const reactPf = profileNames("REACT_PROFILE_TOOLS");
const mobilePf = profileNames("MOBILE_PROFILE_TOOLS");

// ---- classifier helpers ---------------------------------------------------

const OUT_OF_BROWSER = new Set([
  "agent_browser_clipboard_read",
  "agent_browser_clipboard_write",
  "agent_browser_clipboard_copy",
  "agent_browser_clipboard_paste",
]);
const ANTI_TEMPT_PRODUCT_PLUMBING = new Set([
  "agent_browser_install",
  "agent_browser_upgrade",
  "agent_browser_chat",
  "agent_browser_doctor",
  "agent_browser_connect",
  "agent_browser_dashboard_start",
  "agent_browser_dashboard_stop",
  "agent_browser_tools_profiles",
  "agent_browser_session",
  "agent_browser_session_list",
  "agent_browser_session_id",
  "agent_browser_session_info",
  "agent_browser_profiles",
  "agent_browser_skills_list",
  "agent_browser_skills_get",
  "agent_browser_skills_path",
  "agent_browser_plugin_add",
  "agent_browser_plugin_list",
  "agent_browser_plugin_show",
  "agent_browser_plugin_run",
  "agent_browser_stream_enable",
  "agent_browser_stream_disable",
  "agent_browser_stream_status",
  "agent_browser_record_start",
  "agent_browser_record_stop",
  "agent_browser_record_restart",
]);
const SUPERSEDED_BY_DESKTOP = new Map([
  // ab "device" emulation paths exist for mobile testing; we keep desktop
  // and let users emulate by other means.
  ["agent_browser_set_device", "wont-do"],
]);
const DANGEROUS_TOOLS = new Set([
  "agent_browser_eval",
  "agent_browser_cookies_set",
  "agent_browser_cookies_set_curl",
  "agent_browser_cookies_clear",
  "agent_browser_cookies_get",
  "agent_browser_auth_save",
  "agent_browser_auth_login",
  "agent_browser_state_save",
  "agent_browser_state_load",
  "agent_browser_state_list",
  "agent_browser_state_clear",
  "agent_browser_state_show",
  "agent_browser_state_clean",
  "agent_browser_state_rename",
  "agent_browser_set_headers",
  "agent_browser_set_credentials",
  "agent_browser_set_offline",
  "agent_browser_network_route",
  "agent_browser_network_unroute",
  "agent_browser_network_har_start",
  "agent_browser_network_har_stop",
  "agent_browser_storage_set",
  "agent_browser_storage_clear",
  "agent_browser_add_init_script",
  "agent_browser_remove_init_script",
]);

function shortName(n) {
  return n.replace(/^agent_browser_/, "");
}

function scope(n) {
  if (OUT_OF_BROWSER.has(n)) return "out-of-browser";
  // upload/download cross the FS boundary
  if (n === "agent_browser_upload" || n === "agent_browser_download") return "both";
  if (n === "agent_browser_pdf" || n === "agent_browser_screenshot") return "both";
  return "in-browser";
}

// Tier = derived from profile membership.
function tier(n) {
  if (corePf.has(n)) return "core";
  if (debugPf.has(n)) return "value-add";
  if (statePf.has(n)) return "value-add";
  if (networkPf.has(n)) return "value-add";
  if (tabsPf.has(n)) return "value-add";
  if (reactPf.has(n)) return "niche";
  if (mobilePf.has(n)) return "niche";
  return "niche";
}

function impactOf(t) {
  if (t === "core") return "high";
  if (t === "value-add") return "medium";
  return "low";
}

// Ownership classifier (§3.5).
function ownership(n) {
  if (ANTI_TEMPT_PRODUCT_PLUMBING.has(n)) return "wont-do";
  if (SUPERSEDED_BY_DESKTOP.has(n)) return "wont-do";
  const s = scope(n);
  if (s === "out-of-browser") return "everywhere";
  // Heuristics for the "needs complex algorithm" leg — these get the
  // "universal" label so both substrates can ship a thin wrapper.
  const sn = shortName(n);
  const universal = new Set([
    "snapshot",
    "diff_snapshot",
    "screenshot",
    "diff_screenshot",
    "read",
    "wait_for_selector",
    "wait_for_text",
    "wait_for_url",
    "wait_for_load",
    "wait_for_function",
    "batch",
    "annotate_screenshot",
    "get_text",
    "get_html",
    "highlight",
    "inspect",
  ]);
  if (universal.has(sn)) return "universal";
  // Default: belongs to opendia (it owns the browser substrate).
  return "opendia";
}

function wontDoReason(n) {
  if (ANTI_TEMPT_PRODUCT_PLUMBING.has(n)) return "product-plumbing";
  if (SUPERSEDED_BY_DESKTOP.has(n)) return SUPERSEDED_BY_DESKTOP.get(n);
  return null;
}

function ourTool(n, own) {
  if (own === "wont-do") return null;
  const sn = shortName(n);
  if (own === "everywhere") return `everywhere.${sn}`;
  // opendia / universal → Everywhere exposes the tool with the
  // `browser_` prefix (matches OpenDiaToolListBuilder.Prefix in
  // src/Everywhere.Mcp/OpenDia/). For universal rows, an
  // `everywhere.<sn>` counterpart also exists; the matrix tracks the
  // `browser_<sn>` half as canonical (universal-twice clause, SPEC §3.1).
  return `browser_${sn}`;
}

function acceptance(n, own) {
  if (own === "wont-do") return "none";
  if (DANGEROUS_TOOLS.has(n)) return `manual:user`;
  return `bench:${shortName(n)}`;
}

// est_effort heuristic: complex algorithms = L, anything with selectors = M,
// trivial getters/wait-ms = S.
function estEffort(n) {
  const sn = shortName(n);
  if (["snapshot", "diff_snapshot", "annotate_screenshot", "read", "batch", "inspect"].includes(sn)) return "L";
  if (sn.startsWith("get_") || sn.startsWith("wait_") || sn.startsWith("dialog_")) return "S";
  if (sn === "back" || sn === "forward" || sn === "reload") return "S";
  return "M";
}

// ---- load existing for idempotent merge ----------------------------------

let prior = {};
if (existsSync(outPath)) {
  try {
    const data = JSON.parse(readFileSync(outPath, "utf8"));
    if (Array.isArray(data.rows)) {
      for (const r of data.rows) prior[r.ab_command] = r;
    }
  } catch (e) {
    console.error(`warn: existing ${outPath} unreadable, regenerating: ${e.message}`);
  }
}

const rows = names.map((n) => {
  const own = ownership(n);
  const t = tier(n);
  const base = {
    ab_command: n,
    tier: t,
    scope: scope(n),
    ownership: own,
    wont_do_reason: wontDoReason(n),
    our_tool: ourTool(n, own),
    impact: impactOf(t),
    est_effort: estEffort(n),
    opendia_prereq: null,
    acceptance: acceptance(n, own),
    status: "missing",
    last_push_sha: null,
    last_bench_run: null,
    notes: "",
  };
  if (own === "wont-do") {
    base.status = "wont-do";
  }
  // Merge mutable fields back from prior file.
  const p = prior[n];
  if (p) {
    for (const k of ["status", "last_push_sha", "last_bench_run", "notes", "opendia_prereq"]) {
      if (p[k] !== undefined && p[k] !== null && p[k] !== "") base[k] = p[k];
    }
  }
  return base;
});

// Prereq wiring (Phase 1 HARD deps from §6 / §Phase 1 step 1):
const SNAPSHOT_DEPENDENTS = new Set([
  "agent_browser_click", "agent_browser_dblclick", "agent_browser_fill",
  "agent_browser_type", "agent_browser_hover", "agent_browser_focus",
  "agent_browser_check", "agent_browser_uncheck", "agent_browser_select",
  "agent_browser_drag", "agent_browser_scroll_into_view",
  "agent_browser_diff_snapshot", "agent_browser_annotate_screenshot",
  "agent_browser_get_text", "agent_browser_get_html", "agent_browser_get_value",
  "agent_browser_highlight", "agent_browser_inspect",
]);
for (const r of rows) {
  if (SNAPSHOT_DEPENDENTS.has(r.ab_command) && r.ownership !== "wont-do") {
    r.opendia_prereq = "browser_snapshot";
  }
}

const out = {
  schema_version: "1",
  ab_sha: actualSha,
  generated_at: new Date().toISOString(),
  rows,
};

writeFileSync(outPath, JSON.stringify(out, null, 2) + "\n");
const counts = rows.reduce((acc, r) => {
  acc[r.ownership] = (acc[r.ownership] || 0) + 1;
  return acc;
}, {});
console.error(`wrote ${rows.length} rows -> ${outPath}`);
for (const k of Object.keys(counts).sort()) {
  console.error(`  ${k}: ${counts[k]}`);
}
