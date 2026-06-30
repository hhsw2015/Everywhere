# GOAL: Embed OpenCLI site adapters in Everywhere

Self-contained spec for an autonomous `/goal` loop. One repo:
- `~/Dev/Everywhere` (fork `github.com/hhsw2015/Everywhere`)

OpenCLI = `jackwener/opencli`, locked at the latest tag at the time the
loop starts (read `package.json` of the upstream clone). Source tree is
cloned read-only into `3rd/opencli/` for adapter sourcing; the upstream
runtime is NOT used.

---

## 1. Terminal goal

Ship a single Everywhere installer that, on launch, exposes the full
OpenCLI site-adapter library to any MCP client through one tool surface
(`opencli_list` / `opencli_run` / `opencli_describe`). The Everywhere
binary runs each adapter `.js` file unmodified inside an embedded V8
runtime; OpenCLI's own CLI runtime / Chrome extension / local daemon are
NOT bundled.

Verifiable signal:
- `opencli_list` returns ≥ 150 commands across ≥ 100 sites.
- 5 fixed bench adapters pass byte-equal output against the locked
  upstream sha (see §11).
- macOS arm64 published binary tree (`Everywhere.app/Contents/MonoBundle`)
  grows ≤ 35 MB versus the pre-spec baseline. The shipped `.dmg` may
  be smaller after compression; we measure the uncompressed bundle so
  cold-disk cost is honest.
- Single MCP roundtrip for `opencli_run("36kr","news",...)` completes
  ≤ 5 s on a warm V8 (cold start ≤ 8 s).

---

## 2. Invariants

### 2.0 Topology (HARD)

Claude Code still sees **one** MCP server: Everywhere
(`http://127.0.0.1:7878/mcp`). OpenCLI adapters are embedded inside
Everywhere's process; they do NOT spawn subprocesses, run their own
daemon, or open WebSocket sessions. Browser-strategy adapters reach
Chrome through Everywhere's existing OpenDia bridge — no second browser
extension, no parallel browser stack.

### 2.1 Independence (HARD)

- **OpenCLI upstream** is treated as a vendored source library only:
  `clis/**/*.js` is copied into `3rd/opencli/clis/` at build time,
  along with the matching `cli-manifest.json`. No npm-managed runtime
  ends up in the shipped binary; the embedded `Microsoft.ClearScript.V8`
  is the only JS engine.
- **Everywhere** must run with OpenDia uninstalled. Calling
  `opencli_run` for a `strategy: PUBLIC` adapter (no browser) MUST
  succeed; calling it for a browser-strategy adapter MUST return
  `{ok:false, error:"opendia-not-connected"}`, never a synthesised
  fallback.
- **Bundle budget**: macOS arm64 binary increase ≤ 35 MB above the
  pre-spec baseline (recorded in `docs/specs/opencli-bundle-baseline.txt`
  by the first OpenCLI PR). Linux x64 budget is ≤ 50 MB (V8 native is
  larger there). Lint Rule 6 enforces.

### 2.2 MCP tool naming

The new tools live under unprefixed names because OpenCLI is a single
external library, not a substrate twin: `opencli_list`,
`opencli_describe`, `opencli_run`. They join the meta tools
(`list_more_tools`, `call_tool`, `web_search`, `web_fetch_url`) at the
top level.

### 2.3 Anti-temptation (HARD)

- Adapters MUST NOT be rewritten in C#. Translating site logic forks
  it from the upstream maintenance flow and decays within weeks
  (target sites change DOM constantly). The only acceptable port is
  the **runtime contract** (`cli({...})`, `Strategy`, error classes,
  `IPage` shim) — keep that surface ≤ 400 lines C#.
- The `IPage` shim MUST NOT add semantics beyond what OpenDia already
  exposes. If an adapter calls a `page.*` method we cannot map onto
  an existing OpenDia tool, return a structured `{ok:false, error:
  "page.<name>-not-supported"}` and let the run surface the gap. No
  best-effort polyfills.
- Module loader MUST NOT execute any JS outside `3rd/opencli/clis/`.
  Resolving an `import` to a path outside the vendored tree is a hard
  error.

### 2.4 Forbidden

1. Bundling Node.js (the runtime, not just V8), npm, OpenCLI's
   `daemon.ts`, or its Chrome extension into the Everywhere installer.
   ClearScript V8 native binaries (`ClearScriptV8.<rid>.dylib/so/dll`)
   are explicitly allowed — they are V8 alone, with no Node libuv,
   no fs, no `process`. The host shim provides only what §3.4 lists.
2. Network calls from the OpenCliRuntime that aren't initiated by an
   adapter (no telemetry, no upstream version checks at runtime).
3. Mutating any file under `3rd/opencli/`. The directory is read-only
   at runtime; refresh happens through `scripts/sync-opencli.mjs` and
   commits the result.
4. Auto-merging PRs that touch `DANGEROUS_ADAPTERS` (§5.2).
5. Lowering the bench thresholds in §1.
6. Running adapter `.test.js` files in the V8 runtime — only the
   non-test `.js` files register commands.
7. Exposing OpenCLI's `LOCAL` strategy (external CLI hub). That part
   of OpenCLI is intentionally left to the xlinkBook integration, not
   re-implemented in Everywhere.

---

## 3. Capability taxonomy

Two disjoint phases. Adapter assignment to a phase is taken from
`strategy` field on the upstream `cli({...})` registration.

### 3.1 Phase 1 — non-browser strategies

`strategy: 'public'` — adapter does its work via `fetch()` against
public APIs / RSS / GraphQL endpoints. Host shim provides Node-style
`fetch` (mapped to .NET `HttpClient`); no `IPage` is needed. Examples:
`36kr/news`, `hackernews/top`, every adapter that does not import
`page` in its `func` signature.

### 3.2 Phase 2 — browser strategies

`strategy: 'cookie' | 'intercept' | 'ui'` — adapter receives an
`IPage` and operates on a real Chrome tab. Host shim wires
`page.goto/evaluate/wait/click/snapshot/cookies/...` onto the existing
`browser_*` MCP surface (which forwards to OpenDia). Examples:
`bilibili/me` (cookie), `36kr/hot` (DOM scrape, public landing),
`twitter/timeline` (intercept).

### 3.3 Out-of-scope

`strategy: 'local'` — OpenCLI's external-CLI hub. Forbidden by §2.4 #7.

### 3.4 IPage surface (lint-enforced)

Every method that adapters call on `page` MUST appear in
`OpenCliRuntime/IPage.cs` and resolve via OpenDia. Lint Rule 7 reads
`3rd/opencli/clis/**/*.js`, greps for `page.<name>(`, and requires the
union to be a subset of the methods declared in `IPage.cs`. Any new
`page.*` method that ships in upstream forces a SPEC review (failing
Rule 7 with a concrete list of new symbols).

The guaranteed methods (≥ 99% of adapter calls per `grep`):
- `page.goto(url, opts?)` → `browser_page_navigate`
- `page.evaluate(jsString)` → `browser_evaluate_js` (wraps body in
  `return (...)`)
- `page.wait(arg)` → number → `setTimeout`; string → `waitForSelector`
- `page.click(refOrSel)` → `browser_snapshot` + `browser_click`
- `page.screenshot(opts?)` → `browser_screenshot`

Cookie-strategy adapters do NOT call a `page.cookies` accessor —
they reach cookies through `page.evaluate` running in the target
site's origin (cookies ride along on `fetch` automatically). No
host-side cookie plumbing is needed in the IPage shim.

Tail (≤ 1 % of calls): `page.tabs`, `page.snapshot`, `page.type`,
`page.keys`, `page.find`, `page.cdp`. Stubbed with structured errors;
upstream may grow this list — Rule 7 catches that.

---

## 4. Schema contracts

Schemas at `docs/specs/schemas/<name>.v1.json`. Outputs carry
`schema_version: "1"`.

### 4.1 `opencli_list`
```json
{ "schema_version": "1", "commands": [
    { "site": "36kr", "name": "news", "description": "Latest tech/startup news",
      "strategy": "public", "browser": false,
      "args": [{"name":"limit","type":"int","default":20}] }
] }
```
Sourced from the registry without executing adapter `func`. Cold
build: load every `.js` in `3rd/opencli/clis/`, capture `cli({...})`
metadata, drop the closures. Caches under
`<userdata>/opencli-manifest.json` keyed by upstream sha; rebuild only
when the sha changes.

### 4.2 `opencli_describe`
```json
{ "schema_version": "1", "site": "36kr", "name": "news",
  "description": "...", "args": [...], "strategy": "public",
  "browser": false, "columns": ["rank","title","url"] }
```

### 4.3 `opencli_run`
```json
{ "schema_version": "1", "ok": true, "data": <adapter return value>,
  "site": "36kr", "name": "news", "elapsed_ms": 123 }
```
Failure shape:
```json
{ "schema_version": "1", "ok": false,
  "error": "<message>", "code": "<adapter CliError code or RUNTIME_*>",
  "site": "36kr", "name": "news" }
```

### 4.4 Adapter `cli({...})` registration (V8-side, observed)

Subset that the host stores; everything else is dropped:
```ts
{ site, name, description, strategy, browser?, access?, domain?,
  aliases?, args?: Arg[], columns?, func: (page?, args) => Promise }
```

---

## 5. Adapter inventory + matrix

### 5.1 Source

Vendored upstream tree at `3rd/opencli/clis/`. Sync by running
`scripts/sync-opencli.mjs`, which:
1. clones `https://github.com/jackwener/opencli` at the default branch HEAD
2. copies `clis/**/*.js` (excluding `*.test.js`) into `3rd/opencli/clis/`
3. copies `cli-manifest.json` into `3rd/opencli/cli-manifest.json`
4. records the upstream sha into
   `3rd/opencli/UPSTREAM_SHA` (one line, used by lint Rule 5).

Phase 0 sanity-checks: site count ∈ [120, 250], command count ∈
[800, 2500]; out-of-range → HANDOFF.

### 5.2 DANGEROUS_ADAPTERS (no auto-merge)

```
DANGEROUS_ADAPTERS = {
  # Adapters that mutate state on the user's behalf — comment posts,
  # likes, follows. Auto-merge is too dangerous; user must eyeball
  # the run. Verified to exist in upstream cli-manifest.json @ v1.8.5;
  # lint Rule 8 fails if a name drifts out from under the SPEC.
  "bilibili/comment", "bilibili/follow", "bilibili/favorite",
  "twitter/post",     "twitter/follow",  "twitter/unfollow",
  "weibo/post",       "weibo/publish",
  "instagram/comment","instagram/post",  "instagram/follow", "instagram/unfollow",
  "tiktok/comment",   "tiktok/follow",   "tiktok/unfollow",
  "reddit/comment",
  "jike/post",        "jike/comment",    "jike/repost",
}
```
Lint Rule 8 enforces by name match against the manifest at sync time.

### 5.3 wont-do reason codes (closed)

`local-strategy` (LOCAL adapters — out-of-scope, §2.4 #7) |
`auth-flow` (adapter requires interactive QR / 2FA scan we cannot
automate; user must run upstream once to seed cookies) |
`upstream-flake` (upstream marked unstable; revisit on next sync).

### 5.4 `parity-matrix.json` row schema

```json
{
  "site": "36kr",
  "name": "news",
  "strategy": "public",
  "browser": false,
  "tier": "core | value-add | niche",
  "status": "have | wont-do | blocked",
  "wont_do_reason": "code or null",
  "acceptance": "bench:<id> | manual:<who> | none",
  "last_run_ts": "ISO8601 or null",
  "notes": "string"
}
```

`PARITY_MATRIX_OPENCLI.md` is auto-rendered from the JSON.

---

## 6. State machine

Each `/goal` enters step 1.

```
1. Pre-flight (idempotent):
   a. dotnet --version → else HANDOFF dotnet-missing.
   b. ClearScript NuGet resolves → else HANDOFF nuget-missing.
   c. ANTHROPIC_AUTH_TOKEN + ANTHROPIC_BASE_URL (soft for code-only,
      hard for bench).

2. Read SPEC + parity-matrix.json + BLOCKED.md.

3. Pick next adapter:
   - Filter: status=missing, not in BLOCKED, not in DANGEROUS_ADAPTERS.
   - Phase order: Phase 1 (public) before Phase 2 (browser).
   - Within phase, sort: tier=core first, then value-add, then niche.
   - Take top N=5 in one batch.

4. Per batch, the test surface is bounded:
   - The 5 fixed bench fixtures (§11.2) always run.
   - One representative adapter per **strategy bucket** is added to
     `OpenCli/SmokeTests.cs` (PUBLIC fetch, PUBLIC DOM, COOKIE, plus
     one INTERCEPT and one UI when those phases land). Other adapters
     in the batch only get their `(site, name)` recorded in
     `parity-matrix-opencli.json`; they are tested in production by
     real agent calls. DANGEROUS_ADAPTERS never get a test that posts
     state — they get a load-only test that asserts `cli({...})`
     registered, no `func` invocation.

5. Run the smoke + bench tests. Failure → fix forward (max 3 push),
   else row.status = blocked, BLOCKED.md notes the failing
   assertion.

6. CI gate: spec-lint passes; OpenCLI test set is green; bundle
   baseline diff ≤ 35 MB (see Rule 6).

7. Auto-merge gate:
   - If row in DANGEROUS_ADAPTERS: PR stays open, status=blocked,
     notes="user-review-required"; continue.
   - Else: auto-merge into main. The OpenCLI surface is gated by
     `EVERYWHERE_MCP_OPENCLI=1` until Phase 2 ends; without it,
     `opencli_*` tools are filtered out by `CoreToolGate` so users
     who don't opt in see no behaviour change. Cutover (env-on by
     default) is a separate user-driven flip in Phase 4.

8. Update parity-matrix.json. Append to bench-results-opencli.json.

9. Goto step 2. Stop per §7.
```

**Push budget**: per adapter 3 push; total 80 push across this SPEC.
Exceeded → BLOCKED (per-adapter) or HANDOFF budget-exhausted (total).

---

## 7. Done criteria + escape hatches

Loop terminates successfully when:

1. `parity-matrix-opencli.json` parses; SPEC-lint clean.
2. Every row's `status` ∈ {`have`, `blocked`, `wont-do`}.
3. `bench/results/opencli-results.json` has one entry per `bench:*`
   row with status=`have`.
4. For each such entry, the comparison rule is the one §11.4 defines
   for that fixture's adapter strategy:
   - PUBLIC fetch (e.g. RSS / JSON API): byte-equal `data` field
     versus `expected.json`. Drift > 0 bytes triggers manual review.
   - PUBLIC DOM scrape / browser strategy: schema-equal — every key
     in `expected.json` is present, types match, array length is
     within ±20% of the recorded baseline. The full envelope outside
     `data` is still byte-equal.
   The split is justified in §11.4; lint Rule 4 already enforces
   per-fixture mode.
5. macOS arm64 `.dmg` size delta ≤ 35 MB; Linux ≤ 50 MB; Windows ≤
   25 MB. CI publishes the delta.
6. `HANDOFF.md` exists (always generated at exit).
7. PR open on `main` with required sections: `## Adapters added`,
   `## Bundle delta`, `## Bench summary`, `## Known limitations`.
8. Cutover decision: while merging the final PR, the user either
   (a) keeps `opencli_*` gated behind `EVERYWHERE_MCP_OPENCLI=1`
   (default off, opt-in), or (b) flips the gate's default to on by
   editing `CoreToolGate.OpenCliEnabled`. Either is acceptable; the
   loop does not auto-flip.

**Escape hatches**:
- Per-adapter push budget exceeded → row → blocked.
- Total push budget exceeded → HANDOFF budget-exhausted.
- ClearScript V8 segfaults on a specific adapter → blocked, notes
  the input that crashes; do not retry.
- Upstream sha drift mid-run (someone re-syncs) → finish current
  batch, then re-pre-flight from step 1.

**State files** (under `docs/specs/`):
- `parity-matrix-opencli.json` — authoritative state
- `PARITY_MATRIX_OPENCLI.md` — auto-rendered
- `BLOCKED.md` — per-adapter last error + push history
- `HANDOFF.md` — user action list, unconditional at exit
- `opencli-bundle-baseline.txt` — pre-spec binary size, written by
  the first OpenCLI PR.

---

## 8. Phases

### Phase 0: Bootstrap (≤ 4 push)

1. `git fetch && git status` on main.
2. Add `Microsoft.ClearScript.V8` (latest stable, lock minor) +
   per-RID native packages (`Microsoft.ClearScript.V8.Native.osx-arm64`,
   `osx-x64`, `win-x64`, `linux-x64`) to `Everywhere.Mcp.csproj`.
3. Write `scripts/sync-opencli.mjs` (algorithm in §5.1). Run once;
   commit `3rd/opencli/clis/` + `cli-manifest.json` + `UPSTREAM_SHA`.
4. Write `scripts/build-opencli-bundle.mjs` that copies `3rd/opencli/clis/`
   into the publish output under `Resources/opencli/clis/`. Hook
   into `Everywhere.Mac.csproj` / `Everywhere.Linux.csproj` /
   `Everywhere.Windows.csproj` as a `BeforePublish` target. Driving
   it from each platform project (rather than `Everywhere.Mcp.csproj`)
   is intentional: the bundle is shipping artefact, not a build
   artefact of the library.
5. Record current macOS arm64 binary size into
   `docs/specs/opencli-bundle-baseline.txt`.
6. Write `scripts/spec-lint-opencli.mjs` (rules in §9).
7. CI workflow `.github/workflows/spec-lint-opencli.yml`:
   `actions/setup-node@v4` (Node 20). spec-lint only.
8. Commit, push, CI green.

### Phase 1: Runtime + non-browser strategies (≤ 30 push)

Files:
- `src/Everywhere.Mcp/OpenCli/OpenCliRuntime.cs`
- `src/Everywhere.Mcp/OpenCli/HostShim.cs` (`cli`, `Strategy`, errors,
  `fetch` injection)
- `src/Everywhere.Mcp/OpenCli/ModuleLoader.cs` (intercept
  `@jackwener/opencli/registry|errors`; resolve relative `./utils.js`)
- `src/Everywhere.Mcp/OpenCli/AdapterDef.cs` (record type)
- `src/Everywhere.Mcp/OpenCli/IPage.cs` (Phase 1 stubs only — every
  method throws `Phase2NotReady`; Phase 1 adapters never touch them)
- `src/Everywhere.Mcp/Tools/OpenCliTools.cs`
  (`opencli_list`, `opencli_describe`, `opencli_run`)

Tests:
- `tests/Everywhere.Mcp.Tests/OpenCli/RuntimeBootTests.cs` — load 5
  PUBLIC adapters by name, assert non-null `func`.
- `tests/Everywhere.Mcp.Tests/OpenCli/PublicStrategyTests.cs` — run
  `36kr/news`, `hackernews/top` against a stubbed `fetch` host fn that
  returns canned RSS / JSON from `tests/fixtures/opencli/`. No live
  network in CI. The bench harness (§11) is the live-net check; it
  tolerates flake per §11.4.
- `tests/Everywhere.Mcp.Tests/OpenCli/ParityWithNodePoCTests.cs` —
  contract diff against `tests/fixtures/opencli/<site>-<name>-poc.json`
  produced by re-running the Phase 0 Node PoC.

CoreToolGate:
- `opencli_list` → core (entry point)
- `opencli_run` → core (main invoker)
- `opencli_describe` → long-tail (verbose; agent rarely needs it)

Phase 1 ships independently (`v0.10.x` train) once these tests are
green.

### Phase 2: Browser strategies (≤ 25 push)

Files added:
- `src/Everywhere.Mcp/OpenCli/OpenDiaPageBridge.cs` — concrete
  `IPage` that calls `OpenDiaBridge.CallToolAsync(...)` directly
  (in-process; same path `MetaTools.CallTool` uses for the browser_*
  long-tail). One method per row in §3.4. No HTTP self-call.
- `IPage.cs` stubs from Phase 1 are replaced wholesale by the bridge
  implementation; `Phase2NotReady` exception class is deleted.
- Lint Rule 7 enforcement (already wired in Phase 0).

Tests:
- `BrowserStrategyTests.cs` — run `36kr/hot`, `bilibili/hot`,
  `bilibili/me` (cookie). The latter two are `manual` tier and skipped
  in CI; run on the agent's macOS host.

Phase 2 ships as `v0.10.y`.

### Phase 3: Hardening (≤ 15 push)

- Restart-tolerance: V8 engine init must not blow startup latency by
  more than 500 ms. Add a lazy-boot guard so `opencli_*` is the only
  surface that triggers V8 boot; everything else (current MCP tools)
  must keep its current cold-start budget.
- Memory: the registry holds adapter closures forever. Every load
  must `engine.Collect()` after the metadata has been captured;
  closures are re-created on demand inside `Resolve()`.
- Observability: structured log line per `opencli_run` with
  `{site, name, ms, ok, error?}`. No payload logging.

### Phase 4: SUMMARY + PR

Write `bench/SUMMARY-OPENCLI.md` (§10). Open PR `main → main` (or
release branch if user preference). Agent does NOT merge.

### Phase 5: HANDOFF

Generate `docs/specs/HANDOFF.md`. Stop.

---

## 9. SPEC-lint rules (`scripts/spec-lint-opencli.mjs`)

1. `parity-matrix-opencli.json` parses; types correct; enums valid.
2. `status ∈ {have, wont-do, blocked}`.
3. `wont_do_reason` ∈ §5.3 enum, IFF `status=wont-do`.
4. Every non-`wont-do` row has a matching `[McpServerTool]` (the meta
   tool `opencli_run` covers all rows; lint just checks the row's
   `(site, name)` exists in `3rd/opencli/cli-manifest.json`).
5. `3rd/opencli/UPSTREAM_SHA` matches the most recent
   `scripts/sync-opencli.mjs` invocation recorded in git log
   (`refresh: opencli@<sha>` commit subject).
6. Bundle delta vs `opencli-bundle-baseline.txt` ≤ 35 MB on
   macOS arm64; ≤ 50 MB on Linux x64; ≤ 25 MB on Windows x64.
7. Set of `page.<method>` symbols used across
   `3rd/opencli/clis/**/*.js` (excluding `.test.js`) is a subset of
   the methods declared in `OpenCliRuntime/IPage.cs`.
8. `DANGEROUS_ADAPTERS` (§5.2) all exist in
   `3rd/opencli/cli-manifest.json` (typo guard).
9. No `parity-matrix-opencli.json` row references a `(site, name)`
   absent from `3rd/opencli/cli-manifest.json`.
10. No `.test.js` lands in the publish output (regex on the published
    `Resources/opencli/clis/`).
11. `OpenCliRuntime.cs` + `HostShim.cs` + `ModuleLoader.cs` +
    `IPage.cs` + `OpenDiaPageBridge.cs` + `OpenCliTools.cs` total ≤
    2400 LOC (post-trim, excluding comments). The cap forces the team
    to keep the surface thin; bumping it requires SPEC change. 1000 →
    1200 → 1300 → 1800 → 2000 → 2200 → 2400 across successive
    hardening passes — round-4 covers per-adapter pipeline closure,
    Task-cached pipeline runner load, manifest path-traversal guard,
    BufferLike binary fs reads, concurrent stdout/stderr, cross-platform
    shell, and the unified `engine.Execute` gate.
12. Every test in `tests/Everywhere.Mcp.Tests/OpenCli/` has a frontmatter-style
    leading comment indicating the adapter name(s) under test.
13. Each `bench/opencli/fixtures/<id>/expected.json` declares
    `compare: "byte-equal" | "schema-equal"` at top level. PUBLIC
    fetch fixtures must use `byte-equal`; DOM-scrape and browser
    fixtures must use `schema-equal`. The bench harness reads this
    field to choose its diff strategy (§7.4).

---

## 10. SUMMARY-OPENCLI.md schema (Phase 4)

```markdown
# Embed OpenCLI site adapters in Everywhere

## Verdict
<ship | partial-ship | not-yet>. One paragraph.

## Coverage
Total adapters in upstream sha <X>: <n>
- have: <n> (Phase 1: <n>, Phase 2: <n>)
- wont-do: <n>
- blocked: <n>

## wont-do breakdown
| reason_code | count | example commands |

## BLOCKED root causes
| adapter | last error | suggested fix |

## Bench
| fixture | site/name | bytes_ours | bytes_upstream | match | pass |

## Bundle delta
| platform | baseline (MB) | current (MB) | delta (MB) | budget |

## Recommended next step
<merge | open follow-up SPEC | upstream sync>
```

---

## 11. Bench harness

### 11.1 Layout

```
bench/opencli/
  fixtures/<site>-<name>/
    args.json            # input args for the run
    expected.json        # frozen output from the locked upstream sha
  runner/
    run-everywhere.sh    # calls Everywhere MCP via curl
    diff.mjs             # byte-equal compare with tolerance windows
  results/opencli-results.json
```

### 11.2 Five fixed bench adapters (`bench:*`)

| id | site/name | strategy | why |
|----|-----------|----------|-----|
| `36kr-news`        | 36kr/news        | public   | RSS path; tests fetch + xml parsing |
| `pypi-downloads`   | pypi/downloads   | public   | JSON API; tests JSON shape (HN adapters work too via the vendored pipeline runner) |
| `36kr-hot`         | 36kr/hot         | public   | DOM scrape; tests page.evaluate |
| `bilibili-hot`     | bilibili/hot     | public   | second DOM scrape, different site |
| `bilibili-me`      | bilibili/me      | cookie   | tests cookie-via-evaluate path |

The first four are CI-tier (`ci_tier: ci`); the cookie one is `manual`
because it needs a real bilibili session.

### 11.3 Invocation contract

`run-everywhere.sh` issues a single `tools/call` to
`http://127.0.0.1:7878/mcp` with `name=opencli_run` and the args from
`args.json`. The full envelope from §4.3 is what bench compares — so
schema drift is a bench failure, not a logical drift.

### 11.4 `expected.json` freezing

The Phase 0 PoC lives at `bench/opencli/poc/` (committed). Phase 0
ports the working PoC files (`host.mjs`, `loader.mjs`, `run.mjs`,
`run-browser.mjs`, `page.mjs`) into that directory, adjusts the
adapter root path to `3rd/opencli/clis/`, and adds a
`bench/opencli/poc/freeze.mjs` driver:

```bash
node --import "..." bench/opencli/poc/freeze.mjs   # writes expected.json for each fixture
```

`freeze.mjs` runs once per upstream sync; the resulting
`expected.json` is committed alongside `UPSTREAM_SHA`. Lint Rule 5
ties the two together.

DOM-scrape fixtures are inherently flaky against the live web; the
diff harness allows two relaxations:
- `data: []` from a failed live scrape doesn't fail the bench unless
  the previous 3 runs also returned `[]`.
- For the DOM-scrape benches, only the field schema is asserted, not
  individual values. (Rule 4.3 still requires the envelope to match
  byte-for-byte.)

### 11.5 CI tier recipe

OpenCLI bench runs inside the existing `mcp-ci.yml` workflow as part
of `Everywhere.Mcp.Tests` (xUnit). No standalone MCP host process is
spawned; tests instantiate `OpenCliRuntime` + `OpenCliTools`
directly, mock the OpenDia bridge with an in-memory fake for
browser-strategy benches, and compare the tool's JSON output to
`expected.json`.

```yaml
- uses: actions/setup-dotnet@v4
  with: { dotnet-version: '10.0.x' }
- run: |
    dotnet test tests/Everywhere.Mcp.Tests/Everywhere.Mcp.Tests.csproj \
      --filter "FullyQualifiedName~OpenCli.BenchTests"
```

`BenchTests` enumerates the four CI fixtures in §11.2 and asserts
each matches its `expected.json`. The cookie fixture
(`bilibili/me`) lives in `BenchManualTests` and is filtered out of CI
by `[Trait("Tier","manual")]`.

---

## 12. Hand-off (user-only)

1. Review `parity-matrix-opencli.json` + `BLOCKED.md`.
2. Read `bench/SUMMARY-OPENCLI.md`.
3. If satisfied, merge the PR into main. Tag `vX.Y.Z`.
4. Build release on the agent host:
   `bash scripts/build-everywhere-release.sh osx-arm64`.
5. Verify the bundle delta:
   `python3 scripts/check-bundle-delta.py`. Must be ≤ 35 MB.
6. Smoke-test in dev mode:
   ```
   curl -s http://127.0.0.1:7878/mcp -X POST \
     -H 'content-type: application/json' \
     -H 'accept: application/json, text/event-stream' \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/call",
          "params":{"name":"opencli_list","arguments":{}}}' \
     | jq '.result.content[0].text | fromjson | .commands | length'
   ```
   Must return ≥ 150.
7. Run a public-strategy adapter end-to-end:
   ```
   curl -s http://127.0.0.1:7878/mcp -X POST ... \
     -d '{"jsonrpc":"2.0","id":2,"method":"tools/call",
          "params":{"name":"opencli_run",
                    "arguments":{"site":"36kr","name":"news",
                                 "arguments_json":"{\"limit\":3}"}}}'
   ```
   Must return three articles with non-empty titles.

### 12.1 If a `have` adapter fails real use

- Each row's `notes` field links the adapter source +
  the OpenDia method that failed.
- `bench/REPORT_TEMPLATE.md` distinguishes runtime failure (V8 /
  module loader / IPage) from upstream failure (target site changed).

---

## 13. Security

Pragmatic defaults:

- The vendored adapter tree at `3rd/opencli/clis/` is rewritten only
  by `scripts/sync-opencli.mjs` (build-time, run by a developer).
  Inside the published binary it is read-only — V8 cannot write
  anywhere on the filesystem (no `fs` host fn).
- Adapter `func` runs inside a single shared V8 isolate. The isolate
  has no host-fs access except for the read-only `3rd/opencli/clis/`
  loader. We do NOT expose Node fs, child_process, or net. `fetch`
  is the only host fn that reaches the network.
- Cookies stay in OpenDia's existing scope. Browser-strategy adapters
  read cookies through the standard `browser_cookies_get` path,
  which is gated by OpenDia's own permission UI; we don't add a
  parallel cookie store.
- DANGEROUS_ADAPTERS PRs are not auto-merged (§5.2 + §6.7).
- THIRD_PARTY attribution: OpenCLI is MIT; bundle its `LICENSE` in
  `THIRD_PARTY/opencli/LICENSE` and the locked sha in NOTICE.

---

## Appendix A: Why

OpenCLI is the largest open-source library of "real-world site →
high-level command" adapters (~1200 commands across ~170 sites). Its
runtime + Chrome extension + npm install path conflicts with
Everywhere's single-installer model, but the adapter `.js` files
themselves are pure functions over a small `IPage` surface. Embedding
ClearScript V8 lets us run those files unmodified, gaining the entire
upstream catalogue for ~25 MB of native code and ~250 lines of host
shim — versus 5–6 person-months to translate them to C#, after which
they would rot within weeks of upstream changes.

The line we keep is exactly the boundary OpenCLI's own architecture
suggests: a registry of `cli({...})` definitions, an `IPage`
abstraction, and JSON args/columns. We re-implement that boundary in
C# and feed the upstream adapters into it.
