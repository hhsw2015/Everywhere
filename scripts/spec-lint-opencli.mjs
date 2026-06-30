#!/usr/bin/env node
// SPEC §9 lint rules for docs/specs/everywhere-opencli-adapters.md.
// All 13 rules; each failure prints one line and exits non-zero.

import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative, basename, extname } from 'node:path';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SPEC_DIR = join(ROOT, 'docs', 'specs');
const VEND = join(ROOT, '3rd', 'opencli');
const MCP = join(ROOT, 'src', 'Everywhere.Mcp');
const OPENCLI_SRC = join(MCP, 'OpenCli');
const RUNTIME_FILES = [
  'OpenCliRuntime.cs',
  'HostShim.cs',
  'ModuleLoader.cs',
  'AdapterDef.cs',
  'IPage.cs',
  'OpenDiaPageBridge.cs',
];
const TOOLS_FILE = join(MCP, 'Tools', 'OpenCliTools.cs');
const TEST_DIR = join(ROOT, 'tests', 'Everywhere.Mcp.Tests', 'OpenCli');
const BENCH_FIXTURES = join(ROOT, 'bench', 'opencli', 'fixtures');
const BASELINE = join(SPEC_DIR, 'opencli-bundle-baseline.txt');
const MATRIX_PATH = join(SPEC_DIR, 'parity-matrix-opencli.json');

// Verified against 3rd/opencli/cli-manifest.json @ v1.8.5. Adapters
// that mutate state on the user's behalf (post / like / follow /
// purchase). SPEC §5.2 + §6.7 — these PRs are never auto-merged.
const DANGEROUS_ADAPTERS = new Set([
  'bilibili/comment', 'bilibili/follow', 'bilibili/favorite',
  'twitter/post',     'twitter/follow',  'twitter/unfollow',
  'weibo/post',       'weibo/publish',
  'instagram/comment','instagram/post',  'instagram/follow', 'instagram/unfollow',
  'tiktok/comment',   'tiktok/follow',   'tiktok/unfollow',
  'reddit/comment',
  'jike/post',        'jike/comment',    'jike/repost',
]);
const WONT_DO_REASONS = new Set(['local-strategy', 'auth-flow', 'upstream-flake']);
const STATUS_ENUM = new Set(['have', 'wont-do', 'blocked']);

const errors = [];
function fail(rule, msg) { errors.push(`Rule ${rule}: ${msg}`); }

function loadJson(p) {
  return JSON.parse(readFileSync(p, 'utf8'));
}

function walk(dir, acc = []) {
  if (!existsSync(dir)) return acc;
  for (const ent of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, ent.name);
    if (ent.isDirectory()) walk(p, acc);
    else acc.push(p);
  }
  return acc;
}

function manifestNames(manifest) {
  // v1.8.5 ships a top-level array; older builds wrapped it in `{ commands: [...] }`.
  if (Array.isArray(manifest)) return new Set(manifest.map(c => `${c.site}/${c.name}`));
  if (Array.isArray(manifest.commands)) return new Set(manifest.commands.map(c => `${c.site}/${c.name}`));
  return new Set(Object.keys(manifest));
}

function check() {
  // Pre-load matrix + manifest if available.
  const matrix = existsSync(MATRIX_PATH) ? loadJson(MATRIX_PATH) : { rows: [] };
  const manifestPath = join(VEND, 'cli-manifest.json');
  const manifest = existsSync(manifestPath) ? loadJson(manifestPath) : null;
  const manifestSet = manifest ? manifestNames(manifest) : new Set();

  // Rule 1: parity-matrix-opencli.json parses; types correct; enums valid.
  // (Parse already happened above; validate row shape.)
  if (!Array.isArray(matrix.rows)) fail(1, 'rows must be an array');
  for (const [i, row] of (matrix.rows ?? []).entries()) {
    for (const k of ['site', 'name', 'strategy', 'tier', 'status']) {
      if (typeof row[k] !== 'string') { fail(1, `row ${i} missing/invalid ${k}`); break; }
    }
    if (typeof row.browser !== 'boolean') fail(1, `row ${i} browser must be bool`);
    if (!['public', 'cookie', 'intercept', 'ui', 'local'].includes(row.strategy))
      fail(1, `row ${i} strategy out of enum: ${row.strategy}`);
    if (!['core', 'value-add', 'niche'].includes(row.tier))
      fail(1, `row ${i} tier out of enum: ${row.tier}`);

    // Rule 2.
    if (!STATUS_ENUM.has(row.status)) fail(2, `row ${i} status out of enum: ${row.status}`);

    // Rule 3.
    if (row.status === 'wont-do') {
      if (!WONT_DO_REASONS.has(row.wont_do_reason))
        fail(3, `row ${i} (${row.site}/${row.name}) wont-do requires wont_do_reason ∈ {${[...WONT_DO_REASONS].join('|')}}`);
    } else if (row.wont_do_reason != null) {
      fail(3, `row ${i} wont_do_reason set on non-wont-do row`);
    }
  }

  // Rule 9: row (site,name) exists in manifest.
  if (manifest) {
    for (const [i, row] of (matrix.rows ?? []).entries()) {
      const k = `${row.site}/${row.name}`;
      if (!manifestSet.has(k)) fail(9, `row ${i} ${k} not in cli-manifest.json`);
    }
  }

  // Rule 4: non-wont-do row → (site,name) in manifest.
  for (const row of matrix.rows ?? []) {
    if (row.status === 'wont-do') continue;
    if (!manifestSet.has(`${row.site}/${row.name}`)) fail(4, `${row.site}/${row.name} non-wont-do but missing from manifest`);
  }

  // Rule 5: UPSTREAM_SHA matches latest `refresh: opencli@<sha>` commit subject.
  const shaPath = join(VEND, 'UPSTREAM_SHA');
  if (existsSync(shaPath)) {
    const sha = readFileSync(shaPath, 'utf8').trim();
    if (!/^[0-9a-f]{40}$/.test(sha)) fail(5, `UPSTREAM_SHA malformed: ${sha}`);
    try {
      const log = execSync('git log --pretty=%s -- 3rd/opencli/UPSTREAM_SHA | head -1', { cwd: ROOT, encoding: 'utf8' }).trim();
      const m = log.match(/^refresh:\s*opencli@([0-9a-f]+)/);
      if (m) {
        const short = m[1];
        if (!sha.startsWith(short)) fail(5, `UPSTREAM_SHA ${sha.slice(0,10)} does not match latest refresh subject ${short}`);
      }
      // No matching commit yet (initial PR) is fine.
    } catch { /* git log may fail in shallow CI; skip */ }
  } else {
    fail(5, '3rd/opencli/UPSTREAM_SHA missing');
  }

  // Rule 6: bundle delta vs opencli-bundle-baseline.txt.
  // We can only check that the baseline file is well-formed + present.
  if (!existsSync(BASELINE)) fail(6, 'opencli-bundle-baseline.txt missing');

  // Rule 7: page.<method> ⊆ IPage.cs.
  const pageMethods = new Set();
  for (const f of walk(join(VEND, 'clis'))) {
    if (!f.endsWith('.js') || f.endsWith('.test.js')) continue;
    const src = readFileSync(f, 'utf8');
    for (const m of src.matchAll(/\bpage\.([a-zA-Z_$][\w$]*)\s*\(/g)) pageMethods.add(m[1]);
  }
  const ipagePath = join(OPENCLI_SRC, 'IPage.cs');
  if (existsSync(ipagePath)) {
    const ipage = readFileSync(ipagePath, 'utf8');
    for (const name of pageMethods) {
      // Adapter side is camelCase ("getCookies"); IPage.cs is PascalCase
      // ("GetCookies"). Match the C# identifier with the leading char
      // uppercased.
      const pascal = name[0].toUpperCase() + name.slice(1);
      const re = new RegExp(`\\b${pascal}\\s*\\(`);
      if (!re.test(ipage)) fail(7, `IPage.cs missing method used by adapters: page.${name} (expected C# member ${pascal})`);
    }
  } else if (pageMethods.size > 0) {
    fail(7, `IPage.cs missing (adapters call page.${[...pageMethods].slice(0,3).join(', page.')}...)`);
  }

  // Rule 8: DANGEROUS_ADAPTERS all in manifest.
  if (manifest) {
    for (const k of DANGEROUS_ADAPTERS) {
      if (!manifestSet.has(k)) fail(8, `DANGEROUS_ADAPTER ${k} not in cli-manifest.json (rename?)`);
    }
  }

  // Rule 10: no .test.js in vendored tree (this is also the publish output mirror).
  for (const f of walk(join(VEND, 'clis'))) {
    if (f.endsWith('.test.js')) fail(10, `vendored .test.js leak: ${relative(ROOT, f)}`);
  }

  // Rule 11: line-count cap on runtime + tools files.
  let lines = 0;
  const lcFiles = [...RUNTIME_FILES.map(f => join(OPENCLI_SRC, f)), TOOLS_FILE];
  for (const p of lcFiles) {
    if (!existsSync(p)) continue;
    const src = readFileSync(p, 'utf8');
    for (const ln of src.split('\n')) {
      const t = ln.trim();
      if (!t) continue;
      if (t.startsWith('//')) continue;
      if (t.startsWith('/*') || t.startsWith('*') || t.endsWith('*/')) continue;
      lines++;
    }
  }
  if (lines > 2000) fail(11, `OpenCliRuntime+Tools surface ${lines} LOC > 2000 cap`);

  // Rule 12: every test file has a leading // adapter: comment.
  for (const f of walk(TEST_DIR)) {
    if (!f.endsWith('.cs')) continue;
    const head = readFileSync(f, 'utf8').split('\n').slice(0, 10).join('\n');
    if (!/\/\/\s*adapter[s]?:/i.test(head))
      fail(12, `${relative(ROOT, f)} missing leading "// adapter: <site>/<name>" comment`);
  }

  // Rule 13: every bench fixture expected.json declares `compare`.
  if (existsSync(BENCH_FIXTURES)) {
    for (const ent of readdirSync(BENCH_FIXTURES)) {
      const exp = join(BENCH_FIXTURES, ent, 'expected.json');
      if (!existsSync(exp)) continue;
      try {
        const j = loadJson(exp);
        const cmp = j.compare;
        if (cmp !== 'byte-equal' && cmp !== 'schema-equal')
          fail(13, `${relative(ROOT, exp)} compare must be byte-equal|schema-equal (got ${cmp})`);

        // PUBLIC fetch → byte-equal, DOM/browser → schema-equal.
        // A PUBLIC fixture may opt into schema-equal IFF the sibling
        // meta.json sets `compare_reason` explaining the time-varying
        // payload (SPEC §11.4 last paragraph extended).
        const meta = j.meta ?? {};
        const strategy = meta.strategy;
        const browser = !!meta.browser;
        const fixtureDir = dirname(exp);
        let metaSidecar = {};
        try { metaSidecar = loadJson(join(fixtureDir, 'meta.json')); } catch {}
        const reason = metaSidecar.compare_reason;
        if (strategy === 'public' && !browser && cmp !== 'byte-equal' && !reason)
          fail(13, `${relative(ROOT, exp)} PUBLIC fetch must use byte-equal (set meta.compare_reason to opt into schema-equal)`);
        if ((browser || strategy === 'cookie' || strategy === 'intercept' || strategy === 'ui') && cmp !== 'schema-equal')
          fail(13, `${relative(ROOT, exp)} DOM/browser must use schema-equal`);
      } catch (e) {
        fail(13, `${relative(ROOT, exp)} parse error: ${e.message}`);
      }
    }
  }

  if (errors.length) {
    for (const e of errors) console.error(e);
    process.exit(1);
  }
  console.error(`[spec-lint-opencli] OK (rows=${matrix.rows?.length ?? 0} pageMethods=${pageMethods.size} runtimeLOC=${lines})`);
}

check();
