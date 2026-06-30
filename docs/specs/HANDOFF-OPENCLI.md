# OpenCLI adapters — HANDOFF

SPEC: `docs/specs/everywhere-opencli-adapters.md`.
Run summary: `bench/SUMMARY-OPENCLI.md`.

This document is generated unconditionally at exit per SPEC §7.6. Read top
to bottom; everything marked **action** needs a human at some point.

## What landed this run

- **Phase 0** (bootstrap):
  - Vendored upstream `OpenCLI@v1.8.5` (`9161d99d96`) into `3rd/opencli/`
    (1292 adapter `.js` files, `cli-manifest.json`, license).
  - `scripts/sync-opencli.mjs` (refresh tool).
  - `scripts/build-opencli-bundle.mjs` + `src/Build.OpenCliBundle.targets`
    wire `Resources/opencli/{clis,cli-manifest.json}` into each platform
    publish output.
  - `scripts/spec-lint-opencli.mjs` (13 lint rules from SPEC §9).
  - `scripts/render-parity-matrix-opencli.mjs` (deterministic re-render).
  - `scripts/check-bundle-delta.py` (Rule 6 helper for CI).
  - `.github/workflows/spec-lint-opencli.yml` runs lint + render-diff on
    OpenCLI-touching PRs.
  - Schemas: `docs/specs/schemas/opencli_{list,describe,run}.v1.json`.
  - Baseline placeholder: `docs/specs/opencli-bundle-baseline.txt`
    (the first build populates real numbers).

- **Phase 1** (runtime + non-browser strategies):
  - `Microsoft.ClearScript.V8` + per-RID native packages added to
    `Directory.Packages.props` + `src/Everywhere.Mcp/Everywhere.Mcp.csproj`.
  - `src/Everywhere.Mcp/OpenCli/`:
    - `OpenCliRuntime.cs` — lazy-booted V8 isolate, loads every adapter,
      `engine.CollectGarbage(true)` after load.
    - `HostShim.cs` — `cli({...})` registration sink, Node-style `fetch`,
      `console` drain.
    - `ModuleLoader.cs` — rewrites `@jackwener/opencli/{registry,errors}`
      to the in-host shim; resolves relative `./utils.js` against the
      vendored tree.
    - `AdapterDef.cs` — record type for SPEC §4.4 metadata.
    - `IPage.cs` — Phase 1 stub (every method throws
      `Phase2NotReadyException`), specced to be a strict superset of every
      `page.*` symbol grepped from `3rd/opencli/clis/**/*.js`.
  - `src/Everywhere.Mcp/Tools/OpenCliTools.cs` — `opencli_list`,
    `opencli_describe`, `opencli_run`.
  - `src/Everywhere.Mcp/EverywhereMcpServiceExtensions.cs` registers
    `OpenCliRuntime` + `OpenCliTools` in DI; runtime looks for
    `Resources/opencli/` first, falls back to the repo `3rd/opencli/`.
  - `src/Everywhere.Mcp/CoreToolGate.cs` hides `opencli_*` from
    `tools/list` unless `EVERYWHERE_MCP_OPENCLI=1` (SPEC §6.7 default
    opt-in).
  - Tests under `tests/Everywhere.Mcp.Tests/OpenCli/`:
    `RuntimeBootTests`, `PublicStrategyTests`, `ParityWithNodePoCTests`,
    `BrowserStrategyTests`. Each carries the required adapter-comment
    header for Rule 12.

- **Phase 2** (browser strategies):
  - `OpenDiaPageBridge.cs` implements `IPage` over
    `OpenDiaBridge.CallToolAsync`. `OpenCliTools` picks the bridge for
    `browser:true`/`cookie|intercept|ui` adapters; falls back to the
    Phase 1 stub for PUBLIC adapters.
  - Without a connected OpenDia, browser-strategy adapters return the
    canonical `{ok:false, error:"opendia-not-connected"}` envelope from
    §2.1 (never a synthesised fallback).

- **Phase 3** (hardening):
  - Lazy V8 boot — engine creation runs on first `opencli_*` call only.
  - Structured info log per `opencli_run` with `{site, name, ms, ok, code}`
    (no payload logging).

## Action items

1. **Run `dotnet test`.** No local dotnet was available during the
   autonomous run. Restore + test on a host that has the .NET 10 SDK:
   ```
   dotnet restore
   dotnet test tests/Everywhere.Mcp.Tests/Everywhere.Mcp.Tests.csproj \
     --filter "FullyQualifiedName~OpenCli"
   ```

2. **Run a platform release build to populate the bundle baseline.** Lint
   Rule 6 skips zero values. After the first `dotnet publish -r osx-arm64`,
   `du -sk` the publish dir and overwrite the `osx-arm64 0` line in
   `docs/specs/opencli-bundle-baseline.txt` with the real byte count.

3. **Freeze the three browser-strategy fixtures.** On the agent host with
   OpenDia connected, extend `bench/opencli/poc/freeze.mjs` to drive the
   live MCP server (or run `bench/opencli/runner/run-everywhere.sh
   <fixture>` against a connected server) and commit the resulting
   `expected.json` files. Update `parity-matrix-opencli.json` rows from
   `blocked` → `have` and re-render.

4. **Cutover decision (SPEC §6.7).** When merging the final PR pick (a)
   keep `opencli_*` gated behind `EVERYWHERE_MCP_OPENCLI=1` (default off,
   opt-in) or (b) flip `CoreToolGate.OpenCliEnabled`'s default to `true`.
   Either is acceptable; the loop does not auto-flip.

## Known limitations

- HN adapters now ship as `pipeline` definitions; SPEC §2.4 #1 keeps the
  pipeline runner out-of-scope. Drop `wont_do_reason="upstream-flake"`
  once upstream restores the `func` shape (or once the SPEC adopts a thin
  pipeline runner).
- The `OpenDiaPageBridge` method names are best-effort guesses at OpenDia's
  long-tail tool naming (`browser_evaluate_js`, `browser_close_window`).
  Each PR that lights up a new adapter should verify the actual OpenDia
  tool name via `OpenDia.OpenDiaToolSync.AvailableTools`.

## Push budget

Per-adapter: not consumed (no adapter beyond bench:* exercised yet).
Total: < 80 (well under SPEC §6 cap).
