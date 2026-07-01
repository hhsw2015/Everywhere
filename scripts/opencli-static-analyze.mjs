#!/usr/bin/env node
// Static analyzer for OpenCLI adapter .js files. Catches classes of
// bug that only surface at runtime today:
//
//   R1  page.evaluate template lacks `return ` → outer wrap yields undefined
//       (v0.9.289 root cause)
//   R2  fetch("/...") relative URL but no navigateBefore in manifest →
//       base-URL missing at runtime
//   R3  cookie/intercept/ui strategy but browser:false in manifest
//   R4  page.* Promise called without `await` (missing await → race /
//       swallowed error)
//   R5  strategy=public but code uses page.evaluate / page.cdp / page.
//       cookies (strategy misdeclared → won't get bg tab or bridge)
//   R6  page.evaluate expression exceeds ~200 KB (Runtime.evaluate has
//       CDP protocol-level size limits and gets truncated silently)
//   R7  Adapter references result.<prop> without null-guard (v0.9.285
//       symptom: JsonNode passed opaque → typeof result === 'object'
//       passes but result.<prop> === undefined)
//
// Runs against every non-test .js under 3rd/opencli/clis/. Reports per
// adapter, exits non-zero on any hit. Pass --json to emit machine-
// readable results.

import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative, basename, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const VEND = join(ROOT, '3rd', 'opencli');
const CLIS = join(VEND, 'clis');
const MANIFEST = join(VEND, 'cli-manifest.json');

const args = process.argv.slice(2);
const asJson = args.includes('--json');
const onlyRule = (() => {
  const i = args.indexOf('--only');
  return i >= 0 ? args[i + 1] : null;
})();

const manifest = JSON.parse(readFileSync(MANIFEST, 'utf8'));
const cmds = Array.isArray(manifest) ? manifest : manifest.commands || [];

// Index manifest by site/name AND modulePath so we can look up either way.
const byKey = new Map();
const byPath = new Map();
for (const c of cmds) {
  if (c.site && c.name) byKey.set(`${c.site}/${c.name}`, c);
  if (c.modulePath) byPath.set(c.modulePath, c);
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

const files = walk(CLIS)
  .filter(f => f.endsWith('.js'))
  .filter(f => !f.endsWith('.test.js'))
  .filter(f => !f.endsWith('test-utils.js'))
  // Only actual adapter files — must be reachable from manifest
  // modulePath. Filters out shared helpers under clis/<site>/lib/*.
  .filter(f => {
    const rel = relative(CLIS, f);
    return byPath.has(rel);
  });

const results = [];

function isAwaited(source, idx) {
  // Look at ~30 chars before idx for a preceding `await ` token.
  // Also allow `return ` (async return X = same as await X at boundary)
  // or `= ` (const x = page.foo(); pattern — may or may not await; the
  // author might store the promise. Only flag if next non-ws token is
  // `.` (chaining) — that's the "forgot to await" smell).
  const before = source.slice(Math.max(0, idx - 40), idx);
  if (/\bawait\s+$/.test(before)) return true;
  if (/\breturn\s+$/.test(before)) return true;
  // Look ahead to see if there's a .then / .catch chain — that's also
  // valid non-await handling.
  return false;
}

function analyze(file) {
  const rel = relative(CLIS, file);
  const cmd = byPath.get(rel);
  const src = readFileSync(file, 'utf8');
  const hits = [];

  function rec(rule, msg, line = null) {
    if (onlyRule && !onlyRule.split(',').includes(rule)) return;
    hits.push({ rule, msg, line });
  }

  // Cheap line-number lookup
  const lines = src.split('\n');
  function lineOf(idx) {
    let s = 0;
    for (let i = 0; i < lines.length; i++) {
      const end = s + lines[i].length + 1;
      if (idx < end) return i + 1;
      s = end;
    }
    return lines.length;
  }

  // R1: page.evaluate(`...`) — check the template contains `return `
  // Extension wraps: `expr.includes('return ') ? expr : 'return (' + expr + ')'`
  // — if the template already has an inner return (IIFE) the wrap uses
  // the IIFE-as-statement branch, which needs a top-level return that
  // IIFEs don't provide. Adapter should either:
  //   a) not wrap in IIFE (let extension wrap once), OR
  //   b) if IIFE, prepend `return await ` (we do this in the C# shim,
  //      but the shim only kicks in when LooksLikeExpression is true —
  //      relies on template starting with `(`).
  const evalRe = /page\.evaluate\s*\(\s*`([\s\S]*?)`\s*\)/g;
  let m;
  while ((m = evalRe.exec(src)) !== null) {
    const body = m[1];
    const line = lineOf(m.index);
    const trimmed = body.trim();
    // Case: IIFE `(async ...)()` or `(function ...)()`
    if (/^\(\s*(async\s+)?function|\(async\s*\(/.test(trimmed)) {
      // OK if it ends with `()`  — LooksLikeExpression + our C# shim
      // adds `return await`. But template MUST start with `(` after
      // trim (Trim() at both ends since v0.9.289).
      // Also — the IIFE body must have `return X` inside for the
      // caller to receive the value.
      if (!/\breturn\s/.test(body)) {
        rec('R1', `page.evaluate IIFE body has no 'return' — outer wrap yields undefined`, line);
      }
    } else {
      // Bare-body form: must have top-level `return` OR be an expression
      const isExpr = /^\(.*\)\s*$/s.test(trimmed) || /^await\s+/.test(trimmed);
      if (!isExpr && !/\breturn\s/.test(body)) {
        rec('R1', `page.evaluate body has no 'return' and is not an expression`, line);
      }
    }
  }

  // R2: fetch('/...') relative URL requires navigateBefore for base URL
  // Detect at surface: fetch call with a string literal starting with '/'
  const fetchRelRe = /(?<!\.)\bfetch\s*\(\s*[`'"]\/[^`'"]{0,300}/g;
  let hasRelativeFetch = false;
  while ((m = fetchRelRe.exec(src)) !== null) {
    hasRelativeFetch = true;
    break;
  }
  if (hasRelativeFetch && cmd) {
    if (!cmd.navigateBefore || cmd.navigateBefore === false) {
      rec('R2', `uses relative fetch('/...') but manifest.navigateBefore is ${JSON.stringify(cmd.navigateBefore)} — bg tab won't have a base URL`, null);
    }
  }

  // R3: cookie/intercept/ui strategy but browser:false in manifest
  if (cmd) {
    const strat = (cmd.strategy || '').toLowerCase();
    if ((strat === 'cookie' || strat === 'intercept' || strat === 'ui') && cmd.browser !== true) {
      rec('R3', `strategy=${strat} but manifest.browser=${cmd.browser} — OpenCliTools won't attach OpenDia bridge`, null);
    }
  }

  // R4: page.* called without await / return / .then
  // Match all `page.<method>(` occurrences that are NOT preceded by
  // `await`, `return`, `=`, `:` (arg passing), and followed by `(`.
  // Note: SelectTab, Wait, etc. return void — we can't know shape
  // without IPage.cs cross-reference. Skip fire-and-forget ok cases:
  //   - Standalone statements: `page.foo(x);` on its own line
  const pageCallRe = /\bpage\.([a-zA-Z][a-zA-Z0-9]*)\s*\(/g;
  while ((m = pageCallRe.exec(src)) !== null) {
    const method = m[1];
    const idx = m.index;
    const line = lineOf(idx);
    const before = src.slice(Math.max(0, idx - 40), idx);
    // Must be preceded by await, return, `= `, `.then(` (as arg), `,` (arg list), `(` (arg list), or `? :`
    const okContext = /(\bawait\s+|\breturn\s+|=\s*$|\(\s*$|,\s*$|:\s*$|\.then\s*\(\s*$|&&\s*$|\|\|\s*$|\?\s*$|=>\s*$)/.test(before);
    // Statement-level call (no context) — check if followed by `.then(` chained
    const afterMatch = /^\([^;]*\)(\s*\.then|\s*\.catch)/s.exec(src.slice(idx));
    const chainsThen = !!afterMatch;
    if (!okContext && !chainsThen) {
      rec('R4', `page.${method}(...) called without await/return/then — Promise unhandled`, line);
    }
  }

  // R5: browser=false but code uses browser-required page.* methods.
  // OpenCliTools attaches OpenDiaPageBridge when strategy != public OR
  // browser=true; otherwise adapter gets Phase1StubPage which throws
  // on every method. `strategy=public + browser=true` is legit — the
  // bridge is used to fetch through the user's browser (bypass CORS,
  // reuse Cloudflare cookies, etc.) even though no auth is needed.
  if (cmd) {
    const strat = (cmd.strategy || '').toLowerCase();
    if (strat === 'public' && cmd.browser !== true) {
      const usesBrowser = /\bpage\.(evaluate|cdp|snapshot|click|fill|goto|screenshot|find|getCookies)\s*\(/.test(src);
      if (usesBrowser) {
        rec('R5', `strategy=public + browser=${cmd.browser} but code calls browser-only page.*() — will hit Phase1StubPage error`, null);
      }
    }
  }

  // R6: page.evaluate template exceeds ~200 KB
  const evalRe2 = /page\.evaluate(?:WithArgs)?\s*\(\s*`([\s\S]*?)`/g;
  while ((m = evalRe2.exec(src)) !== null) {
    if (m[1].length > 200_000) {
      rec('R6', `page.evaluate expression is ${m[1].length} bytes (>200KB) — may hit CDP Runtime.evaluate limit`, lineOf(m.index));
    }
  }

  // R7: `page.evaluate(...)` result accessed without null-guard AND
  // adapter throws generic guard message. Heuristic: after `const X =
  // await page.evaluate`, next 500 chars must contain either an
  // `X === null` / `!X` / `typeof X` check, OR pass through .kind
  // pattern (adapter handles kind envelope). We only warn if neither.
  const evalAssignRe = /(?:const|let|var)\s+(\w+)\s*=\s*await\s+page\.evaluate(?:WithArgs)?\s*\(/g;
  while ((m = evalAssignRe.exec(src)) !== null) {
    const varName = m[1];
    const line = lineOf(m.index);
    const scope = src.slice(m.index, m.index + 2000);
    // Guarded if adapter does any of: explicit === null / !X / typeof
    // X / X.kind envelope / X && Y / X?. optional chain / X?.[ /
    // X ?? Y nullish coalesce / return X (final result caller checks).
    const guarded = new RegExp(
      `(?:` +
        `\\b${varName}\\s*(?:===|==|!==|!=)\\s*(?:null|undefined)` +
        `|\\bif\\s*\\(\\s*!?${varName}\\b` +
        `|\\btypeof\\s+${varName}\\s*(?:===|!==)` +
        `|\\b${varName}\\.kind\\b` +
        `|\\b${varName}\\s*&&` +
        `|\\b${varName}\\s*\\?\\??\\s*[\\.\\[]` +
        `|\\b${varName}\\s*\\?\\?` +
        `|\\breturn\\s+${varName}\\b` +
      `)`
    ).test(scope);
    if (!guarded) {
      rec('R7', `result of page.evaluate stored in '${varName}' but next 2000 chars have no null-guard / kind check`, line);
    }
  }

  return { file: rel, cmd: cmd ? `${cmd.site}/${cmd.name}` : rel, strategy: cmd?.strategy, hits };
}

for (const f of files) results.push(analyze(f));

const hitsByRule = {};
let totalHits = 0;
for (const r of results) {
  for (const h of r.hits) {
    hitsByRule[h.rule] = (hitsByRule[h.rule] || 0) + 1;
    totalHits++;
  }
}

if (asJson) {
  console.log(JSON.stringify({
    total: results.length,
    withHits: results.filter(r => r.hits.length > 0).length,
    totalHits,
    byRule: hitsByRule,
    results: results.filter(r => r.hits.length > 0),
  }, null, 2));
} else {
  console.log(`[opencli-static] analyzed ${results.length} adapter files`);
  for (const r of results) {
    if (r.hits.length === 0) continue;
    console.log(`\n${r.cmd}  (strategy=${r.strategy})`);
    for (const h of r.hits) {
      console.log(`  ${h.rule}${h.line ? `  L${h.line}` : ''}  ${h.msg}`);
    }
  }
  console.log(`\n[summary] ${totalHits} hits across ${results.filter(r => r.hits.length > 0).length} adapters`);
  console.log('[by rule]', hitsByRule);
}

process.exit(totalHits > 0 ? 1 : 0);
