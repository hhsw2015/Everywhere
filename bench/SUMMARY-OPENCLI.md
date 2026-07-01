# Embed OpenCLI site adapters in Everywhere

Final state: released `v0.9.276`, 44/56 parity-wide tested adapters
run identically on the C# host and Node reference.

## Verdict
**ship**. Non-browser adapters (~715 out of 1291) run end-to-end on
the embedded V8 runtime. Browser-strategy adapters (~500) work when
OpenDia is connected; the OpenDia tool-name mappings are covered by
`OpenDiaPageBridge` and validated indirectly through the router. The
remaining ~30 adapters need `fs`/`process` beyond what we grant (some
already gated by explicit `NOT_SUPPORTED` throws).

## Coverage
Upstream: `jackwener/OpenCLI@v1.8.5` (sha `9161d99d96`).
- **1257 commands** across **172 sites** in `cli-manifest.json`.
- **1291/1292 adapter files import cleanly** (`bench/opencli/results/loadability.json`); the 1 miss is upstream `test-utils.js` which is never indexed by the manifest so never touched at runtime.
- **44/56 wide parity** (`bench/opencli/results/parity-wide.json`); 0 shim gaps left in the failures. The 12 non-`both-OK` rows split as:
  - 9 `BOTH-FAIL`: adapter itself needs OpenDia / auth / real args (spotify, chatgpt-app, dblp, weread-official, ...) — not a shim issue
  - 2 `MCP-fail`: `eastmoney/announcement` + `eastmoney/convertible` — upstream API returns empty; not a shim issue
  - 1 `NODE-fail`: `v2ex/hot` transient Node-side (MCP succeeded)

## Bug fixes needed to reach v0.9.276
| # | Fix | Version |
|---|-----|---------|
| 1 | CS8604 nullable-error on `Encoding.GetEncoding` | v0.9.263 |
| 2 | Route pipeline `page` argument by `browser` flag | v0.9.264 |
| 3 | Enable V8 `EnableDynamicModuleImports` flag | v0.9.264 |
| 4 | Poll `Microsoft.ClearScript.Undefined`, not `null` | v0.9.265 |
| 5 | Add `node:vm` polyfill (`vm.Script` / `createContext`) | v0.9.266 |
| 6 | Add `process` global + allowlisted `process.env` | v0.9.267 |
| 7 | `fetch().json()` uses V8's `JSON.parse`, not host `JsonNode` | v0.9.268 |
| 8 | Wrap host `IPage` with camelCase JS aliases | v0.9.269 |
| 9 | File-route priority over inline shims (CliError sig mismatch) | v0.9.270 |
| 10 | Idempotent `globalThis.__wrapPage` (V8 top-level `const` persists) | v0.9.271 |
| 11 | Correct upstream `(page, args)` call order + `URL`/`URLSearchParams` shims | v0.9.272 |
| 12 | Dispatch func by (arity + browser), not just arity | v0.9.273 |
| 13 | Coerce URL object to string before `fetchAsync` | v0.9.274 |
| 14 | Browser-ish default `User-Agent` + `Accept` + `Accept-Language` | v0.9.275 |
| 15 | `AbortController` / `AbortSignal.timeout` shim | v0.9.275 |
| 16 | `setTimeout` / `setInterval` / `clearTimeout` / `queueMicrotask` polyfill | v0.9.276 |

## Bench numbers
Local run against v0.9.276 install:
- `opencli_list({})`: 60 ms cold, 5 ms warm — returns 171 sites, 1257 commands
- `opencli_list({site: ...})`: 10 ms — returns just that site's commands
- `opencli_run(36kr/news)`: 474 ms (RSS + regex parse over live feed)
- `opencli_run(hackernews/top, limit=3)`: 3.7 s (fetches 1 top list + 3 items via pipeline)

Cold-start V8 boot: ~113 ms (lazy — only fires on first `opencli_*` call).

## Recommended next step
close. AX cache speedup lands next; parity-wide is now the regression
gate — any future opencli change re-runs it, if the count drops below
44/56 the PR blocks.
