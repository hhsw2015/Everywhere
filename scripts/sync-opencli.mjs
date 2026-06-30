#!/usr/bin/env node
// SPEC §5.1 — vendors upstream OpenCLI adapter tree read-only.
//
// Output:
//   3rd/opencli/clis/**/*.js   (non-test .js files only)
//   3rd/opencli/cli-manifest.json
//   3rd/opencli/UPSTREAM_SHA   (one line)
//   THIRD_PARTY/opencli/LICENSE
//
// Locks to the latest GitHub tag (`refs/tags/v*`). Override:
//   SYNC_REF=<tag|sha|branch>  node scripts/sync-opencli.mjs

import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, mkdirSync, writeFileSync, readdirSync, existsSync, readFileSync, copyFileSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const DEST = join(ROOT, '3rd', 'opencli');
const LICENSE_DEST = join(ROOT, 'THIRD_PARTY', 'opencli');
const UPSTREAM = 'https://github.com/jackwener/OpenCLI.git';

function sh(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { stdio: ['ignore', 'pipe', 'inherit'], encoding: 'utf8', ...opts });
  if (r.status !== 0) throw new Error(`${cmd} ${args.join(' ')} exited ${r.status}`);
  return r.stdout.trim();
}

function resolveRef() {
  if (process.env.SYNC_REF) return process.env.SYNC_REF;
  const out = sh('git', ['ls-remote', '--tags', '--sort=-v:refname', UPSTREAM]);
  const tags = out.split('\n')
    .map(l => l.split('\t')[1])
    .filter(r => r && /refs\/tags\/v\d+\.\d+\.\d+$/.test(r))
    .map(r => r.replace('refs/tags/', ''));
  if (!tags.length) throw new Error('no v* tags on upstream');
  return tags[0];
}

function walk(dir) {
  const out = [];
  for (const ent of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, ent.name);
    if (ent.isDirectory()) out.push(...walk(p));
    else out.push(p);
  }
  return out;
}

function main() {
  const ref = resolveRef();
  const tmp = mkdtempSync(join(tmpdir(), 'opencli-sync-'));
  try {
    console.error(`[sync-opencli] cloning ${UPSTREAM} @ ${ref}`);
    sh('git', ['clone', '--depth=1', '--branch', ref, UPSTREAM, tmp]);
    const sha = sh('git', ['-C', tmp, 'rev-parse', 'HEAD']);

    if (existsSync(join(DEST, 'clis'))) rmSync(join(DEST, 'clis'), { recursive: true, force: true });
    if (existsSync(join(DEST, 'runtime'))) rmSync(join(DEST, 'runtime'), { recursive: true, force: true });
    mkdirSync(join(DEST, 'clis'), { recursive: true });
    mkdirSync(LICENSE_DEST, { recursive: true });

    const srcClis = join(tmp, 'clis');
    let copied = 0;
    let siteSet = new Set();
    for (const abs of walk(srcClis)) {
      const rel = relative(srcClis, abs);
      if (!rel.endsWith('.js')) continue;
      if (rel.endsWith('.test.js')) continue;
      const out = join(DEST, 'clis', rel);
      mkdirSync(dirname(out), { recursive: true });
      copyFileSync(abs, out);
      copied++;
      const site = rel.split('/')[0];
      if (site && !site.startsWith('_')) siteSet.add(site);
    }

    // Vendor a tiny subset of dist/src/ — only what the pipeline runner
    // and its sibling modules need. Adapter-side bare imports of
    // `@jackwener/opencli/{errors,utils,logger,interceptor,pipeline}`
    // route to these files via OpenCliDocumentLoader.fileRoutes so
    // adapter and runtime observe the SAME module instance.
    const runtimeFiles = [
      'errors.js', 'logger.js', 'utils.js', 'capabilityRouting.js',
      'interceptor.js', 'manifest-types.js', 'types.js', 'version.js',
      'constants.js',
    ];
    const runtimeDirs = ['pipeline', 'pipeline/steps', 'browser'];
    for (const f of runtimeFiles) {
      const from = join(tmp, 'dist', 'src', f);
      if (!existsSync(from)) continue;
      const out = join(DEST, 'runtime', f);
      mkdirSync(dirname(out), { recursive: true });
      copyFileSync(from, out);
    }
    for (const d of runtimeDirs) {
      const dir = join(tmp, 'dist', 'src', d);
      if (!existsSync(dir)) continue;
      for (const ent of readdirSync(dir, { withFileTypes: true })) {
        if (!ent.isFile() || !ent.name.endsWith('.js')) continue;
        if (ent.name.endsWith('.test.js')) continue;
        const out = join(DEST, 'runtime', d, ent.name);
        mkdirSync(dirname(out), { recursive: true });
        copyFileSync(join(dir, ent.name), out);
      }
    }
    // Patches required by the embedded runtime — applied verbatim to
    // every freshly-synced runtime tree:
    //   1. utils.js: stub out `import TurndownService from 'turndown'`
    //      since we don't bundle that package.
    //   2. pipeline/steps/download.js: replace the body with a stub
    //      that throws NOT_SUPPORTED — upstream pulls node:stream and
    //      external CLIs we don't bundle.
    const utilsPath = join(DEST, 'runtime', 'utils.js');
    if (existsSync(utilsPath)) {
      const orig = readFileSync(utilsPath, 'utf8');
      const patched = orig.replace(
        /^import TurndownService from 'turndown';$/m,
        '// Embedded-runtime patch: stubbed turndown (not bundled).\n' +
        "const TurndownService = function () { throw new Error('turndown is not bundled in the embedded runtime; use opencli/utils:htmlToMarkdown shim instead'); };");
      writeFileSync(utilsPath, patched);
    }
    const dlStepPath = join(DEST, 'runtime', 'pipeline', 'steps', 'download.js');
    if (existsSync(dlStepPath)) {
      writeFileSync(dlStepPath,
        "// Replaced by Everywhere — see scripts/sync-opencli.mjs.\n" +
        "import { CliError } from '../../errors.js';\n" +
        "export async function stepDownload(_page, _params, _data, _args) {\n" +
        "    throw new CliError('NOT_SUPPORTED', 'pipeline.download is not implemented in the embedded runtime');\n" +
        "}\n");
    }

    copyFileSync(join(tmp, 'cli-manifest.json'), join(DEST, 'cli-manifest.json'));
    copyFileSync(join(tmp, 'LICENSE'), join(LICENSE_DEST, 'LICENSE'));
    writeFileSync(join(DEST, 'UPSTREAM_SHA'), sha + '\n');
    writeFileSync(join(DEST, 'UPSTREAM_REF'), ref + '\n');

    // Phase 0 sanity (SPEC §5.1).
    const manifest = JSON.parse(readFileSync(join(DEST, 'cli-manifest.json'), 'utf8'));
    const cmdCount = Array.isArray(manifest.commands) ? manifest.commands.length
      : Array.isArray(manifest) ? manifest.length
      : Object.keys(manifest).length;
    const siteCount = siteSet.size;
    console.error(`[sync-opencli] copied=${copied} sites=${siteCount} manifest_commands=${cmdCount} sha=${sha}`);
    if (siteCount < 120 || siteCount > 250) throw new Error(`HANDOFF: site count ${siteCount} out of [120, 250]`);
    if (cmdCount < 800 || cmdCount > 2500) throw new Error(`HANDOFF: command count ${cmdCount} out of [800, 2500]`);

    console.error(`[sync-opencli] OK. Suggested commit subject: refresh: opencli@${sha.slice(0, 10)}`);
  } finally {
    try { rmSync(tmp, { recursive: true, force: true }); } catch {}
  }
}

main();
