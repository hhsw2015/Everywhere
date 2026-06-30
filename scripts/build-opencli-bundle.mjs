#!/usr/bin/env node
// SPEC §8 Phase 0 step 4 — copies the vendored adapter tree into the
// publish output as `Resources/opencli/clis/`. Hooked into each
// platform .csproj as a BeforePublish target.
//
// CLI:
//   node scripts/build-opencli-bundle.mjs --src 3rd/opencli \
//                                          --out <publishDir>/Resources/opencli
//
// Lint Rule 10 forbids `.test.js` from landing here; the vendored tree
// is already test-stripped by sync-opencli.mjs, but this script
// re-asserts just in case.

import { mkdirSync, copyFileSync, readdirSync, statSync, existsSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { join, dirname, relative, resolve } from 'node:path';

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  return i >= 0 ? process.argv[i + 1] : fallback;
}

const SRC = resolve(arg('src', '3rd/opencli'));
const OUT = resolve(arg('out', 'publish/Resources/opencli'));

function walk(dir, acc = []) {
  for (const ent of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, ent.name);
    if (ent.isDirectory()) walk(p, acc);
    else acc.push(p);
  }
  return acc;
}

function copyTree(srcRoot, dstRoot, filter) {
  let count = 0;
  for (const abs of walk(srcRoot)) {
    const rel = relative(srcRoot, abs);
    if (!filter(rel)) continue;
    const out = join(dstRoot, rel);
    mkdirSync(dirname(out), { recursive: true });
    copyFileSync(abs, out);
    count++;
  }
  return count;
}

function main() {
  if (!existsSync(SRC)) {
    console.error(`[build-opencli-bundle] skip: no source tree at ${SRC}`);
    return;
  }
  if (existsSync(OUT)) rmSync(OUT, { recursive: true, force: true });
  mkdirSync(OUT, { recursive: true });

  const clisOut = join(OUT, 'clis');
  const n = copyTree(join(SRC, 'clis'), clisOut, rel => rel.endsWith('.js') && !rel.endsWith('.test.js'));

  copyFileSync(join(SRC, 'cli-manifest.json'), join(OUT, 'cli-manifest.json'));
  copyFileSync(join(SRC, 'UPSTREAM_SHA'), join(OUT, 'UPSTREAM_SHA'));
  if (existsSync(join(SRC, 'UPSTREAM_REF'))) copyFileSync(join(SRC, 'UPSTREAM_REF'), join(OUT, 'UPSTREAM_REF'));

  console.error(`[build-opencli-bundle] copied ${n} adapters → ${OUT}`);
}

main();
