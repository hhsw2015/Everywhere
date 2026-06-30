#!/usr/bin/env node
// Render docs/specs/PARITY_MATRIX_OPENCLI.md from parity-matrix-opencli.json.

import { readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SRC = join(ROOT, 'docs', 'specs', 'parity-matrix-opencli.json');
const DST = join(ROOT, 'docs', 'specs', 'PARITY_MATRIX_OPENCLI.md');

const data = JSON.parse(readFileSync(SRC, 'utf8'));
const rows = data.rows ?? [];

const counts = { have: 0, 'wont-do': 0, blocked: 0 };
for (const r of rows) counts[r.status] = (counts[r.status] ?? 0) + 1;

const head = [
  '# OpenCLI parity matrix',
  '',
  `Auto-rendered from \`parity-matrix-opencli.json\` (do not edit).`,
  `Upstream sha: \`${data.upstream_sha ?? '?'}\` (\`${data.upstream_ref ?? '?'}\`).`,
  '',
  `**Totals**: have=${counts.have}, wont-do=${counts['wont-do']}, blocked=${counts.blocked}.`,
  '',
  '| site | name | strategy | browser | tier | status | acceptance | notes |',
  '|------|------|----------|---------|------|--------|------------|-------|',
];

const sorted = [...rows].sort((a, b) =>
  a.status.localeCompare(b.status) || a.site.localeCompare(b.site) || a.name.localeCompare(b.name));

const lines = sorted.map(r =>
  `| ${r.site} | ${r.name} | ${r.strategy} | ${r.browser ? 'yes' : 'no'} | ${r.tier} | ${r.status} | ${r.acceptance ?? ''} | ${r.notes ?? ''} |`);

writeFileSync(DST, head.concat(lines, ['']).join('\n'));
console.error(`[render-parity-matrix-opencli] wrote ${rows.length} rows`);
