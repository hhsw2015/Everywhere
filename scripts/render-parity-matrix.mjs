#!/usr/bin/env node
// Render docs/specs/PARITY_MATRIX.md from parity-matrix.json. Never
// hand-edited; SPEC step 5 says lint enforces this is regenerated.
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const IN = resolve("docs/specs/parity-matrix.json");
const OUT = resolve("docs/specs/PARITY_MATRIX.md");
const data = JSON.parse(readFileSync(IN, "utf8"));
const rows = data.rows;
const summary = rows.reduce((acc, r) => { acc[r.ownership] = (acc[r.ownership] || 0) + 1; return acc; }, {});

const out = [];
out.push(`# PARITY_MATRIX — agent-browser → Everywhere + OpenDia`);
out.push("");
out.push(`Auto-rendered from \`parity-matrix.json\` (sha \`${data.ab_sha}\`).`);
out.push("DO NOT EDIT BY HAND. Run \`node scripts/render-parity-matrix.mjs\`.");
out.push("");
out.push(`## Summary`);
out.push("");
out.push(`- total: **${rows.length}**`);
for (const k of Object.keys(summary).sort()) {
  out.push(`- ${k}: **${summary[k]}**`);
}
out.push("");
out.push(`## Rows`);
out.push("");
out.push("| ab_command | tier | scope | ownership | our_tool | status | acceptance |");
out.push("|---|---|---|---|---|---|---|");
for (const r of rows) {
  out.push(`| ${r.ab_command} | ${r.tier} | ${r.scope} | ${r.ownership} | ${r.our_tool ?? ""} | ${r.status} | ${r.acceptance ?? ""} |`);
}
out.push("");
writeFileSync(OUT, out.join("\n"));
console.error(`wrote ${OUT} (${rows.length} rows)`);
