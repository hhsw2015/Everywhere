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
import { mkdtempSync, rmSync, cpSync, mkdirSync, writeFileSync, readdirSync, statSync, existsSync, readFileSync, copyFileSync } from 'node:fs';
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
