# OpenCLI adapters — HANDOFF (closed)

SPEC: `docs/specs/everywhere-opencli-adapters.md`.
Run summary: `bench/SUMMARY-OPENCLI.md`.

**Status:** released `v0.9.276`. All original SPEC action items closed.
The parity-wide regression test (44/56 as of v0.9.276) is now the
merge gate for any future OpenCLI change.

## What shipped

- **Phase 0** bootstrap (sync / bundle / lint / CI / schemas / baseline).
- **Phase 1** V8 runtime + PUBLIC func adapters (~600 commands runnable).
- **Phase 2** browser bridge — `OpenDiaPageBridge` routes camelCase
  adapter calls through `OpenDiaBridge.CallToolAsync`; wrap layer maps
  every PascalCase C# method to its JS name so `page.goto(...)` etc. resolve.
- **Phase 3** hardening — lazy V8 boot, `engine.CollectGarbage(true)`,
  structured logs, `_invokeGate`-serialised `engine.Execute`.
- **Phase 4** pipeline runner — vendored upstream
  `@jackwener/opencli/pipeline` at `3rd/opencli/runtime/pipeline/`;
  synthesised per-adapter closures via `EnsureAdapterLoadedAsync` so
  `hackernews/top` and ~115 other pipeline-only adapters run without
  us hand-implementing the DSL. Adapter-side `import { CliError } from
  '@jackwener/opencli/errors'` and runtime-side
  `from '../../errors.js'` both resolve to the SAME vendored file, so
  `instanceof CliError` works across the boundary.
- **Phase 5** Node-compat surface — polyfills for `URL`,
  `URLSearchParams`, `AbortController`, `AbortSignal.timeout`,
  `setTimeout`/`setInterval`/`queueMicrotask`, `process.env` (allow-
  listed), `process.stderr.write`, `node:vm` (`vm.Script` /
  `createContext`), `node:path`/`os`/`crypto`/`fs`/`child_process`.
  `fs`/`child_process` back onto real host resources (with 2-min
  execSync cap, concurrent stdout/stderr, real UTF-8/base64 encoding).
- **Testing infrastructure**:
  - `scripts/test-opencli-loadability.mjs` — every .js imports cleanly
    (1291/1292 currently).
  - `scripts/test-opencli-runnability.mjs` — sample invoke matrix.
  - `scripts/test-opencli-parity.mjs` — 20-adapter Node vs MCP diff.
  - `scripts/test-opencli-parity-wide.mjs` — auto-picks every default-
    args-satisfiable PUBLIC adapter (56 currently), aggregates errors
    by kind so shim gaps cluster in one pass. **This is the regression
    gate.**

## Cutover status (SPEC §6.7)

`CoreToolGate.OpenCliEnabled` defaults **on** — the 3-tool surface
(`opencli_list` / `describe` / `run`) costs ~600 tokens in the system
prompt, the 1257 commands are reachable lazily. Set
`EVERYWHERE_MCP_OPENCLI=0` to opt out.

## No open action items

The SPEC's original action list (dotnet test, bundle baseline, freeze
browser fixtures, cutover decision) all closed:
- `dotnet test` gate: covered by `.github/workflows/mcp-ci.yml`.
- Bundle baseline: recorded on first `dotnet publish -r osx-arm64`
  release and enforced by `scripts/check-bundle-delta.py`.
- Browser fixtures: `36kr/hot` verified live on the installed
  v0.9.276 (see v0.9.272 → v0.9.273 fix chain; parity-wide picks up
  any regression).
- Cutover: default-on committed in v0.9.261.

## Regressions to watch for

Any change under `src/Everywhere.Mcp/OpenCli/` or
`3rd/opencli/runtime/` should run
`node scripts/test-opencli-parity-wide.mjs` against an Everywhere
install. Numbers to hold or beat:
- 44 both-OK / 56 tested
- 0 shim-side `MCP-fail`

If the shim-side MCP-fail count grows, that's a real regression. If
the both-OK count changes because upstream 3rd-party APIs shifted
their response shapes, update the specific adapter's expected result
and re-baseline.

## Known won't-fix

- `eastmoney/announcement` + `eastmoney/convertible` return empty
  from upstream API (needs a real trading day).
- `spotify/*`, `chatgpt-app/*`, `weread-official/*`, `dblp/author`
  require auth cookies / running client apps — out of scope for
  headless embedded runtime.
- Node-Buffer round-trip on `crypto.digest()`: returns hex string, not
  a Buffer. Only breaks adapters that binary-chain digests.
- DNS-rebinding SSRF hardening (would need `SocketsHttpHandler.
  ConnectCallback`) — deferred; embedded V8 sandbox is the real trust
  boundary and we pin `cli-manifest.json` to a vetted sha.
