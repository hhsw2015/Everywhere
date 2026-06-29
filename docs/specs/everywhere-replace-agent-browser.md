# GOAL: Replace agent-browser with Everywhere + OpenDia

Self-contained spec for an autonomous `/goal` loop. Two repos:
- `~/Dev/Everywhere` (fork `github.com/hhsw2015/Everywhere`)
- `~/Dev/opendia`    (fork `github.com/hhsw2015/opendia`)

ab = `vercel-labs/agent-browser`, locked at
`ed2e10598c9064aecfaeb7cf21b540684db4be2c` (v0.31.1, 2026-06-29).

---

## 1. Terminal goal

Make `Everywhere + OpenDia` jointly capable of replacing ab **for this
user's own agent loop** (not a public-product claim). Each component
stays an independent product.

Verifiable signal: every `bench:*` fixture passes both
`correctness ≥ 0.95` AND `tokens_median(ours) ≤ tokens_median(ab) * 1.10`.

---

## 2. Invariants

### 2.0 Topology (HARD)

Claude Code sees **one** MCP server: Everywhere (`http://127.0.0.1:7878/mcp`).
Everywhere's MCP surface is the union of:
- **Local tools** — macOS a11y, Finder, clipboard, doc readers, etc.
  prefixed `everywhere.<name>`.
- **Forwarded tools** — browser capabilities prefixed `browser_<name>`.
  Everywhere holds a WebSocket client to the OpenDia browser extension
  (the extension is the WS server) and proxies these tool calls through
  that WS link.

OpenDia is a **standalone WS service** (the browser extension is the WS
server). Everywhere is a WS client; any other process speaking the same
WS protocol is equally a client. OpenDia is **not** packaged or used as
an MCP server in this SPEC, and the `opendia-mcp/` directory in the
OpenDia repo (its own legacy MCP shim) is not part of this design.
Cross-repo lint Rule 4 therefore inspects `src/Everywhere.Mcp/Tools/`
(C# tool registrations on the Everywhere side), not `opendia-mcp/`.

### 2.1 Independence (HARD)

- **OpenDia (browser extension)** is a standalone WS service.
  Everywhere is one client of that service; any other process speaking
  the same WS protocol gets the same surface. OpenDia is **not** an
  MCP server in this SPEC, and no SPEC artifact assumes one. Any ab
  capability claimed replaceable by OpenDia must close-loop inside the
  extension's WS API alone — HTML→md, a11y tree rendering with refs,
  `diff_snapshot`, `annotate_screenshot`, `batch`. From the
  Everywhere-as-client side, every `browser_<name>` MCP tool MUST be
  exactly one WS round-trip — no chained extension calls and no
  Everywhere-side fallbacks.
- **Everywhere** must run with OpenDia uninstalled. Local
  (`everywhere.*`) tools must not depend on the extension being
  present. Calling an `browser_*` tool with no extension connected
  returns `{ok:false, error:"opendia-not-connected"}`, never an
  Everywhere-side simulation.
- **OpenDia bundle budget**: extension assets +≤ 50 KB minified above
  pre-SPEC baseline (turndown-lite + fast-diff inlined). Documented in
  OpenDia README delta as OpenDia's own product invariant.

### 2.2 MCP tool naming

Universal capabilities use substrate prefix at the MCP boundary:
`browser_click`, `everywhere.click`. Exclusive-substrate tools use
unprefixed names (no clash exists).

### 2.3 Anti-temptation (HARD)

If a tool returns data that enters agent context AND raw form is
materially larger than minimised, minimisation happens inside the
producing substrate. Trigger: raw > 5 KB OR raw/min > 3× OR caller
can't predict size.

OpenDia must compress HTML→md before return. Both sides screenshot
jpeg q70 default. Both sides prune a11y tree before return. Raw forms
(`get_html`, `screenshot --png`) exist as explicit-opt-in tools whose
description follows §3.4 templates.

### 2.4 Forbidden

1. Browser-internal capabilities in Everywhere; filesystem capabilities
   in OpenDia.
2. Returning raw HTML from OpenDia for "Everywhere to compress later".
3. PNG screenshots by default.
4. Auto-merging PRs touching `DANGEROUS_TOOLS` (§5.2).
5. Merging to upstream `Sylinko/Everywhere` or `iFurySt/opendia`.
6. Lowering bench thresholds in §1.
7. Backup re-implementation of one side's capability inside the other.
8. Silent skips (use `BLOCKED` or `wont-do`).

---

## 3. Capability taxonomy

Three disjoint classes. Every tool fits exactly one.

### 3.1 Universal (both substrates implement)

A universal capability ships **twice** — once inside the OpenDia browser
extension (so the extension stays usable by any WS client that isn't
Everywhere) and once on the Everywhere side (so Everywhere's own
cross-substrate flows, `everywhere.batch`, and local-only macOS paths
work). The two implementations are independent; neither calls the other
back. The Everywhere `browser_<name>` MCP tool forwards to the
extension's WS op of the same name; the `everywhere.<name>` tool runs
the macOS-side equivalent.

| capability | OpenDia | Everywhere |
|---|---|---|
| `snapshot` | DOM/ARIA tree text + ref map | macOS a11y tree text + indices |
| `click` / `fill` | DOM ref | a11y `element_index` |
| `screenshot` | viewport JPEG q70 | window/screen JPEG q70 |
| `wait_for` | kind: `selector\|text\|url\|load_state\|predicate` | kind: `predicate` |
| `diff_snapshot` | vs cached prior OpenDia snapshot | vs cached prior Everywhere snapshot |
| `annotate_screenshot` | overlay DOM refs | overlay a11y indices |
| `read_text` | current tab → md | URL → md (`web_read_url`) and local HTML → md (`doc_read_html`) |
| `batch` | OpenDia tools only | Everywhere tools, may dispatch into OpenDia |
| `get_clipboard` / `set_clipboard` | page clipboard | NSPasteboard |

### 3.2 OpenDia-exclusive

navigate / back / forward / reload / pushstate / tab_* / frame_switch /
window_new / dialog_accept / dialog_dismiss / pdf_print / cookies_* /
localStorage_* / sessionStorage_* / network_capture / har_export /
route_mock / set_offline / set_headers / set_credentials / set_viewport /
set_device / set_geo / set_media / eval / add_init_script / add_style /
remove_init_script / state_save / state_load / get_history /
get_bookmarks / console_capture / errors_capture /
cursor_interactive_promotion / `elementFromPoint`-blocker (inside
`click`) / find_by_role / find_by_text / find_by_label /
find_by_placeholder / find_by_testid / get_text / get_html / get_value /
get_attr / get_box / get_styles / is_visible / is_enabled / is_checked /
auth_save / auth_login / auth_list

### 3.3 Everywhere-exclusive

doc_read_pdf|docx|xlsx|pptx|epub|html|txt (shipped) /
get_finder_selection (shipped) / get_focused_context / get_app_state /
get_app_context / list_apps / read_pick / pick_element /
read_whiteboard / read_whiteboard_image / get_selected_text /
get_terminal_output / get_idle_time / macOS app-level click / set_value /
scroll / press_key / type_text / drag / perform_secondary_action /
AX click blocker (AXHitTest) / web_read_url(url) / cross-substrate
batch

### 3.4 Tool description templates (lint-enforced)

Cheap universal tools MUST contain: `"prefer this for"`.
Raw / verbose tools MUST contain: `"typically large"` AND
`"only when ... loses needed detail"`.

### 3.5 Decision flow for a new ab command

1. Out-of-browser scope (file / NSPasteboard / desktop a11y)? → Everywhere only.
2. In-browser, no complex algorithm (cookies / navigate / wait_for_selector / eval)? → OpenDia only.
3. In-browser WITH complex algorithm (HTML→md / a11y render / diff)? → OpenDia must do it in-substrate per §2.3; if Everywhere also has an entry point, implement a second version there (universal).
4. ab CLI-only (install / dashboard / chat / doctor / trace / record / react devtools / vitals)? → §5.3 classifier; usually wont-do.

---

## 4. Schema contracts (universal tools)

Each schema at `docs/specs/schemas/<name>.v1.json`. Every output carries
`schema_version: "1"`. Bumping requires SPEC change.

### 4.1 `snapshot`
```json
{ "schema_version": "1", "text": "string", "ref_map": {"<ref>": {"role":"string","name":"string","frame_id":"string|null"}}, "truncated": false, "source": {"kind":"browser|app","id":"string"} }
```

### 4.2 `screenshot`
```json
{ "schema_version": "1", "format":"jpeg|png|webp", "base64":"string", "width":1920, "height":1080, "annotations": null }
```
Default `format=jpeg quality=70`.

### 4.3 `read_text`
```json
{ "schema_version": "1", "text":"string", "metadata":{"title":"string|null","url":"string|null","source":"browser_read_text|everywhere.web_read_url|everywhere.doc_read_html","truncated":false} }
```
Cross-implementation Jaccard ≥ 0.85 asserted only on `kind:static_html`
fixtures (lint Rule 16).

### 4.4 `diff_snapshot`
```json
{ "schema_version": "1", "diff":"string", "additions":0, "removals":0, "unchanged":0, "changed":false }
```
Each side caches most-recent snapshot per session. Cache invalidates on
navigate / app-switch.

### 4.5 `wait_for`
```json
{ "schema_version": "1", "satisfied":true, "kind":"selector|text|url|load_state|predicate", "matched":"string|null", "elapsed_ms":0 }
```
Poll cadence: OpenDia 100ms, Everywhere 200ms. Default timeout 25s.

### 4.6 `batch`
```json
{ "schema_version": "1", "results":[{"step":0,"tool":"click","substrate":"opendia|everywhere","success":true,"result":{},"error":null}], "bail_on_error": false }
```
Best-effort by default.

### 4.7 `annotate_screenshot`
`screenshot` shape with `annotations: [{ref, box:[x,y,w,h], role, name}]`.
Implicitly takes a fresh snapshot (refs may invalidate).

---

## 5. ab baseline (locked) + matrix

### 5.1 Extract

Source of truth: 152 `agent_browser_*` MCP tool registrations in
`cli/src/mcp.rs` of the locked sha. Extraction:

```
grep -oE '\.tool\(\s*"agent_browser_[a-z_]+"' \
  /tmp/agent-browser/cli/src/mcp.rs \
  | grep -oE '"agent_browser_[a-z_]+"' | sort -u
```

Phase 0 sanity-checks count ∈ [140, 170]; out-of-range → HANDOFF.

### 5.2 DANGEROUS_TOOLS (no auto-merge)

```
DANGEROUS_TOOLS = {
  browser_eval, browser_cdp_evaluate,
  browser_cookies_set, browser_cookies_clear, browser_cookies_get,
  browser_auth_save, browser_auth_login,
  browser_state_save, browser_state_load,
  browser_route_mock,
  browser_set_headers, browser_set_credentials,
  browser_network_capture, browser_har_export,
  browser_add_init_script, browser_add_style,
  browser_localStorage_set, browser_sessionStorage_set
}
```

### 5.3 wont-do reason codes (closed)

`product-plumbing` (ab self-management) | `dev-tooling` (trace /
profiler / record / react / vitals) | `ios-mobile` (Appium / iOS) |
`ab-cli-only` (ab-shape-unique: confirm/deny prompts, dashboard) |
`superseded-by:<tool>` (folded into another; regex
`^superseded-by:[a-z]+\.[a-z_]+$`).

### 5.4 `parity-matrix.json` row schema

```json
{
  "ab_command": "agent_browser_open",
  "tier": "core|value-add|niche",
  "scope": "in-browser|out-of-browser|both",
  "ownership": "opendia|everywhere|universal|wont-do",
  "wont_do_reason": "code or null",
  "our_tool": "browser_navigate or null",
  "impact": "high|medium|low",
  "est_effort": "S|M|L",
  "opendia_prereq": "tool-id or null",
  "acceptance": "bench:<id>|manual:<who>|none",
  "status": "missing|in-progress|have|blocked",
  "last_push_sha": "string or null",
  "last_bench_run": "ISO8601 or null",
  "notes": "string"
}
```

`PARITY_MATRIX.md` is auto-rendered from `.json` for human review; never
hand-edited.

---

## 6. State machine

Each `/goal` enters step 1.

```
1. Pre-flight (idempotent):
   a. If ~/Dev/opendia missing → git clone hhsw2015/opendia.
      Failure → HANDOFF opendia-unreachable.
   b. opendia origin remote matches hhsw2015 fork? else HANDOFF.
   c. rustc --version → else HANDOFF rust-missing.
   d. node --version ≥ v20 → else HANDOFF node-missing.
   e. ANTHROPIC_AUTH_TOKEN + ANTHROPIC_BASE_URL (soft-fail for
      code-only pushes; hard-required for bench).

2. Read SPEC + parity-matrix.json + BLOCKED.md.

3. Pick next capability:
   - Filter: status=missing, ownership≠wont-do, not in BLOCKED.
   - Universal rows: require opendia_prereq.status=have (or null).
   - Sort: impact desc, ownership=opendia first among universal pairs,
     est_effort asc.
   - Take top 1.

4. Branch by ownership: opendia/everywhere/universal (opendia half
   first).

5. Implement + minimal unit test + push. ONE PR per capability.

6. CI gate (§1 thresholds): spec-lint passes; unit test green; if
   acceptance=bench:<id> AND fixture is ci_tier=ci, fixture must pass.
   `manual` fixtures defer to Phase 3.

7. Auto-merge gate:
   - If row's our_tool ∈ DANGEROUS_TOOLS (§5.2): PR stays open;
     row → status=blocked notes="user-review-required"; continue.
   - Else CI green: auto-merge into experiment/replace-ab.

8. Update parity-matrix.json. Append to bench-results.json.

9. Goto step 2. Stop per §7.
```

**Cross-repo sequencing**: the OpenDia browser extension lands the WS
op first (in `opendia-extension/src/`), then Everywhere lands the C#
parity tool that calls it. Lint Rule 4 grep target is
`Everywhere.Mcp/Tools/Parity/**/*.cs` — looking for an
`[McpServerTool(Name = "browser_<name>")]` (or the project's
equivalent attribute) for every universal-row `our_tool`. No
intermediate manifest, no race.

**Everywhere-exclusive rows** are NOT subject to this lock; they run
in parallel with Phase 1.

**Push budget**: per capability 5 push; total across both repos 100
push. Exceeded → BLOCKED (per-cap) or HANDOFF budget-exhausted (total).

---

## 7. Done criteria + escape hatches

Loop terminates successfully when:

1. `parity-matrix.json` parses; SPEC-lint clean.
2. Every row's `status` ∈ {`have`, `blocked`, `wont-do`}. `blocked`
   rows are explicit "tried and couldn't"; reviewer reads BLOCKED.md.
3. `bench/results/bench-results.json` has one entry per `bench:*` row
   with `status=have`.
4. For each such entry: `correctness ≥ 0.95` AND
   `tokens_median(ours) ≤ tokens_median(ab_frozen) * 1.10`, where
   `tokens_median(ab_frozen)` reads from `expected.json` (Phase 0.5).
   ab is NEVER re-recorded at gate time. Lint Rule 15 enforces
   byte-equality.
5. `HANDOFF.md` exists (always generated at exit).
6. CI green on latest experiment/replace-ab sha in both repos.
7. `bench/SUMMARY.md` matches §10 schema (generated only when Done
   criteria 1-6 met).
8. PR `experiment/replace-ab → main` open on BOTH repos with required
   sections: `## Tools added`, `## Pass rate`, `## Dependencies`, `##
   Known limitations`. Agent does NOT merge to main.

**Escape hatches**:
- Per-cap push budget exceeded → row → BLOCKED.
- Total budget exceeded → HANDOFF budget-exhausted, exit.
- Bench flake (replay-server / judge LLM / extension load) → 3 retries,
  exp backoff, then BLOCKED + `bench-flake-log.md`. (ab never invoked
  live; "ab unavailable" is not a runtime mode.)

**State files** (all under `docs/specs/`):
- `parity-matrix.json` — authoritative state
- `PARITY_MATRIX.md` — auto-rendered view
- `PARITY_DRIFT.md` — ab upstream drift (only via manual command;
  SPEC ignores)
- `BLOCKED.md` — per-cap last error + push history + suggested next move
- `HANDOFF.md` — user action list, unconditional at exit

---

## 8. Phases

### Phase 0: Bootstrap (≤ 5 push on Everywhere)

1. `git fetch && git status` on main; create `experiment/replace-ab`.
2. Clone ab at locked sha; `cd /tmp/agent-browser && cargo build
   --release`. Copy ab `LICENSE` to
   `THIRD_PARTY/agent-browser/LICENSE` in BOTH repos; write `NOTICE`
   crediting the sha.
3. `cd ~/Dev/opendia && git fetch && git checkout -b
   experiment/replace-ab` (clone happened in pre-flight 1a).
4. Write `scripts/extract-ab-commands.mjs` (algorithm §5.1). Run it →
   `docs/specs/parity-matrix.json`. Defaults: `tier`/`impact` from
   mcp.rs tool grouping (core→high etc.), `scope` heuristic from name,
   `ownership` via §3.5, `status=missing`,
   `acceptance=bench:<cmd>` for non-wont-do rows.
5. Write bench harness skeletons (`bench/runner/run-ab.sh`,
   `run-ours.sh`, `judge.ts`, `replay-server.mjs`); empty
   `bench/fixtures/`, `bench/results/bench-results.json: []`.
6. Write `scripts/spec-lint.mjs` (rules in §9).
7. CI workflow `.github/workflows/spec-lint.yml`:
   `actions/setup-node@v4` (`node-version: 20`); runs spec-lint only;
   for cross-repo lint clones `hhsw2015/opendia` into
   `./opendia-readonly/` read-only. **Bootstrap CI = spec-lint only.**
8. Commit, push, CI green.

### Phase 0.5: Freeze ab baselines (≤ 3 push on Everywhere)

For each `bench:*` row: run `run-ab.sh` 5× against local replay
server; freeze `bench/fixtures/<id>/expected.json` with
`{answer, tokens_runs[5], tokens_median, ab_sha, frozen_at}`.

**Concurrency + budget**:
- Up to 4 fixtures in parallel (`BENCH_CONCURRENCY=4`).
- Wall-time cap 4 hours. Exceeded → incomplete fixtures →
  `status=blocked phase-0.5-budget-exhausted`.
- Per-fixture timeout 5 min for all 5 runs.

### Phase 1: OpenDia (in `~/Dev/opendia experiment/replace-ab`)

Rows where `ownership ∈ {opendia, universal}`. State-machine sort
(§6 step 3) drives order. HARD dep constraints override sort:

1. `browser_snapshot` before any ref-dependent tool (click, fill,
   hover, wait_for[predicate], diff_snapshot, annotate_screenshot).
2. `browser_wait_for` before any bench fixture needing DOM quiescence,
   specifically before any `diff_snapshot` bench fixture (Rule 11).
3. `read_text` inlines turndown-lite (≤ 30 KB) + fast-diff (≤ 8 KB)
   into bundle. Rule 8 enforces.

First OpenDia PR records `docs/specs/opendia-bundle-baseline.txt`
(byte size of `dist/` pre-SPEC). Rule 8 lints against it.

Each PR is **two halves**:
- **OpenDia half** (in `~/Dev/opendia/opendia-extension/`): add the WS
  op (handler in `background.js` / `content.js`); Vitest unit test;
  bump `dist/` baseline if needed.
- **Everywhere half** (in `~/Dev/Everywhere/Everywhere.Mcp/Tools/Parity/`):
  add the `[McpServerTool(Name="browser_<name>")]` C# class that calls
  Everywhere's WS client to the OpenDia extension; xUnit unit test;
  tool description follows §3.4 templates.

Cross-repo visibility: SPEC-lint reads the Everywhere C# parity surface
directly (grep `Everywhere.Mcp/Tools/Parity/**/*.cs` for `Name =
"browser_<short>"`). The OpenDia repo carries no SPEC-driven manifest;
its half of the contract is the WS protocol it implements, verified by
its own Vitest suite.

### Phase 2: Everywhere (in `~/Dev/Everywhere`, parallel with Phase 1
for ownership=everywhere; sequenced for ownership=universal)

Priority:
1. `everywhere.web_read_url`
2. `compact_tree` integration into `SnapshotRenderer` (folded into the
   `everywhere.snapshot` row, no separate row)
3. `everywhere.diff_snapshot` (host-side cache of prior `get_app_state`
   per app)
4. AX click blocker (folded into existing `click` tool)
5. `everywhere.annotate_screenshot` (SkiaSharp)
6. `everywhere.batch` (cross-substrate)
7. `everywhere.wait_for[predicate]`

### Phase 3: Bench convergence

Run every `bench:*` fixture against ours + against frozen ab; record
into `bench-results.json`. CI runs `ci_tier=ci` fixtures; `manual`
fixtures run on the agent's host. Per-cap push budget applies.

### Phase 4: SUMMARY + dual PRs

Write `bench/SUMMARY.md` (§10). Open `experiment/replace-ab → main`
PRs on BOTH repos with required body sections. Agent does NOT merge.

### Phase 5: HANDOFF

Generate `docs/specs/HANDOFF.md`. Stop.

---

## 9. SPEC-lint rules (`scripts/spec-lint.mjs`)

1. `parity-matrix.json` parses; types correct; enums valid.
2. `ownership ∈ {opendia, everywhere, universal, wont-do}`.
3. `wont_do_reason` ∈ §5.3 enum OR regex `^superseded-by:[a-z]+\.[a-z_]+$`,
   IFF `ownership=wont-do`.
4. Every non-`wont-do` row with `our_tool=browser_<name>` must have a
   matching `[McpServerTool(Name = "browser_<name>")]` (case-insensitive
   spaces around `=`) somewhere under
   `Everywhere.Mcp/Tools/Parity/**/*.cs`. The OpenDia WS-op
   counterpart is verified by OpenDia's own Vitest CI; SPEC-lint does
   not look at the OpenDia repo for this rule.
5. Every `bench:<id>` has matching `bench/fixtures/<id>/{task.md,
   page/}`.
6. §3.1/3.2/3.3 lists are subsets of matrix rows with matching
   ownership.
7. Universal tools use `browser_` or `everywhere.` prefix.
8. OpenDia `dist/` byte size ≤ baseline + 50 KB (baseline written by
   first OpenDia PR in `docs/specs/opendia-bundle-baseline.txt`).
9. No `task.md` references `https://` URL not also vendored in `page/`.
10. Every fixture front-matter has `ci_tier ∈ {ci, manual}`.
11. Fixtures whose row's `our_tool=diff_snapshot` (or `task.md` invokes
    it) MUST declare `wait_for:` predicate in front-matter.
12. Bidirectional: bench ids in non-wont-do rows == dirs in
    `bench/fixtures/`.
13. Tool description templates (§3.4) regex match against
    `Everywhere.Mcp/Tools/**/*.cs` (both `everywhere.*` and `browser_*`
    parity wrappers live here). OpenDia extension descriptions are not
    SPEC-driven (its own product surface).
14. JSON-aware secret scan on `bench-results.json`: flag string values
    matching `^(?i:authorization|cookie|set-cookie)\s*[:=]\s*\S+`.
15. For every `status=have` bench row,
    `bench-results.json.ab.tokens_median ==
    expected.json.tokens_median` byte-for-byte.
16. Every fixture front-matter has `kind ∈ {static_html, har_replay}`.
17. `ci_tier=ci` fixtures invoke `browser_*` tools only (no
    `everywhere.*` references in `task.md`) — `browser_*` is the
    browser-only subset of Everywhere's MCP surface, runnable in
    headless CI; `everywhere.*` needs a macOS host.
18. Anti-temptation (§2.3) enforced **by code review only**, not by
    lint. Acknowledged debt.

---

## 10. SUMMARY.md schema (Phase 4, success path only)

```markdown
# Everywhere + OpenDia vs agent-browser

## Verdict
<ship | partial-ship | not-yet>. One paragraph.

## Coverage
Total ab tools: 152
- have: <n>
- wont-do: <n>
- blocked: <n>

## wont-do breakdown
| reason_code | count | example tools |

## BLOCKED root causes
| capability | last error | suggested fix |

## Bench deltas
| fixture | correctness | tokens_ours | tokens_ab | ratio | pass |

## Recommended next step
<merge | open follow-up SPEC | revise threshold>
```

---

## 11. Bench harness (Appendix)

### 11.1 Layout

```
bench/
  fixtures/<id>/
    task.md            # YAML front-matter + one-line goal
    page/              # frozen page (HTML for static_html, HAR for har_replay)
    expected.json      # frozen ab baseline
  runner/
    run-ab.sh <id>
    run-ours.sh <id>
    replay-server.mjs  # node 20+, serves page/ via file:// or HAR replay
    judge.ts           # LLM majority + tie-breaker
    system-prompt.md   # identical on both sides
  results/bench-results.json
  REPORT_TEMPLATE.md   # user failure-report template
```

### 11.2 Fixture front-matter

```yaml
---
id: open-arxiv-2303-10130
ci_tier: ci             # ci | manual
kind: static_html       # static_html | har_replay
wait_for: ".result-loaded"   # required if id involves diff_snapshot
---
Open the local arxiv page; report the title.
```

### 11.3 Invocation contract

Both `run-ab.sh` and `run-ours.sh` invoke Claude Code CLI as
subprocess: model `claude-sonnet-4-6`, temperature 0, identical system
prompt at `bench/runner/system-prompt.md`. `ANTHROPIC_AUTH_TOKEN` +
`ANTHROPIC_BASE_URL` from env. Fresh agent session per fixture. ab side
sees only ab tools; ours side sees only OpenDia + Everywhere tools.

### 11.4 `bench-results.json` row

```json
{
  "fixture": "open-arxiv-2303-10130",
  "run_ts": "ISO8601",
  "ab": { "tokens_runs":[..5..], "tokens_median":12340, "answer":"..." },
  "ours": { "tokens_runs":[..5..], "tokens_median":11200, "answer":"..." },
  "correctness": 1.0,
  "judge_votes": [1,1,1],
  "token_ratio": 0.907,
  "pass": true
}
```

- N=5 runs per side per fixture. `tokens_median` = drop min+max,
  median of remaining 3.
- Variance guard: if `(max-min)/median > 0.20` on either side, re-run
  the 5-run set once. Second violation → `status=blocked` reason
  `bench-variance-too-high`.
- `pass = correctness ≥ 0.95 AND token_ratio ≤ 1.10 AND NOT
  flake-suspected`.
- `ab.tokens_median` is read from frozen `expected.json` (never
  re-recorded). Lint Rule 15.

### 11.5 CI tier recipe (for `ci_tier=ci` fixtures)

```yaml
- uses: actions/setup-node@v4
  with: { node-version: 20 }
- run: |
    sudo apt-get install -y xvfb chromium-browser jq
    cd ~/Dev/opendia && pnpm install && pnpm build
- run: |
    xvfb-run --auto-servernum chromium-browser \
      --headless=new \
      --user-data-dir=/tmp/cdp-profile \
      --load-extension=$PWD/opendia-extension/dist \
      --disable-extensions-except=$PWD/opendia-extension/dist &
    sleep 3
    bash bench/runner/run-ours.sh <fixture-id>
```

Manual-tier fixtures (touch Everywhere desktop tools) run only on
agent's macOS host in Phase 3.

### 11.6 Judge (`judge.ts`)

- Pre-check: substring match against `expected.answer`. Match →
  `correctness=1.0`, skip LLM.
- Round 1: 3 LLM calls (`claude-sonnet-4-6`, temp 0). Each ∈ {0, 1}.
- 3/3 → 1.0. 0/3 → 0.0. 1/3 or 2/3 → tie-breaker.
- Tie-breaker: N=4 additional judges. `correctness = sum/7`.
  `≥ 0.95` requires unanimous 7/7.
- All votes persisted in `bench-results.json.judge_votes`.

---

## 12. Hand-off (user-only)

1. Review `parity-matrix.json` + `BLOCKED.md`. Flip rows you disagree
   with.
2. Read `bench/SUMMARY.md`.
3. Merge the two PRs (squash recommended).
4. Release: tag `vX.Y.Z` on Everywhere main. Windows release continues
   to fail on SimplySignDesktop (env issue, unrelated). LFS budget:
   `lfs:false` already set in release workflows.
5. Build OpenDia: `cd ~/Dev/opendia && pnpm install && pnpm build`.
   Install unpacked extension from `opendia-extension/dist/`.
6. Quit + relaunch Everywhere App; restart Claude Code.
7. Verify tool list:
   ```
   curl -s http://localhost:7878/mcp -X POST \
     -H 'content-type: application/json' \
     -H 'accept: application/json, text/event-stream' \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
     | jq -r '.result.tools[].name' | sort
   ```
8. Run one sanity bench fixture manually.

### 12.1 If a `have` row fails real use

- Each row's `notes` field links to owner repo + implementing file.
- `npm install -g agent-browser` reinstalls ab as side-by-side
  fallback.
- `bench/REPORT_TEMPLATE.md` distinguishes substrate failure (wrong
  data) from routing failure (wrong tool chosen).

---

## 13. Security (this is mostly the user's own machine)

Pragmatic defaults; SPEC-noted but NOT enforced gates:

- localhost MCP token unauthenticated.
- Per-origin confirm for `eval`/`cookies_set`/`route_mock`/`state_save`.
- HAR / state_save redaction.
- Sensitive-app screenshot exclusion.
- `doc_read_*` path allowlist.

**Enforced** (real bite):
1. `ANTHROPIC_AUTH_TOKEN` env-only; `.gitignore` + pre-commit
   secret-scan in Phase 0 step 7's CI.
2. Lint Rule 14: bench-results.json scrubbed of headers.
3. `DANGEROUS_TOOLS` PRs not auto-merged (§6 step 7).
4. THIRD_PARTY attribution to ab Apache-2.0 (Phase 0 step 2).

---

## Appendix C: Why

ab proved an agent-driven browser-automation product can be a single
~152-tool surface. Everywhere ships a partial surface — strong on
desktop perception, weak on driving the user's browser — and delegates
the latter to OpenDia. This SPEC closes the gap to ab on the browser
side while keeping the desktop-perception advantage ab does not have.
Replacement = an agent given the combined Everywhere + OpenDia toolset
never needs ab for tasks ab can complete; and can complete tasks ab
cannot (Finder, native macOS apps, doc readers, cross-substrate
sequencing).
