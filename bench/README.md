# bench — Phase 0.5 / Phase 3 harness

SPEC: `docs/specs/everywhere-replace-agent-browser.md` §11.

The harness compares **ab** (locked sha `ed2e10598c9064aecfaeb7cf21b540684db4be2c`,
v0.31.1) against **ours** (Everywhere + OpenDia) on a frozen set of
fixtures. Pass criterion (SPEC §1):
`correctness ≥ 0.95 AND tokens_median(ours) ≤ tokens_median(ab) * 1.10`.

## One-time machine setup

1. Build the ab Rust binary (used by `run-ab.sh`):

   ```bash
   cd /tmp && rm -rf agent-browser
   git clone https://github.com/vercel-labs/agent-browser.git
   cd agent-browser && git checkout ed2e10598c9064aecfaeb7cf21b540684db4be2c
   cd cli && cargo build --release
   ```

   The resulting binary lives at `/tmp/agent-browser/cli/target/release/agent-browser`.
   `run-ab.sh` re-checks the sha each invocation.

2. Build Everywhere for `--mcp` stdio mode:

   ```bash
   cd ~/Dev/Everywhere
   dotnet publish src/Everywhere.Mac/Everywhere.Mac.csproj -c Release -r osx-arm64
   ```

   Set `EVERYWHERE_BIN` if your publish path differs from
   `src/Everywhere.Mac/bin/Release/net10.0/osx-arm64/publish/Everywhere`.

3. Build the OpenDia extension `dist/`:

   ```bash
   cd ~/Dev/opendia/opendia-extension
   npm ci && npm run build:chrome
   ```

4. Launch Chrome with the OpenDia extension loaded **and connected to
   Everywhere's WS bridge**:

   ```bash
   /Applications/Google\ Chrome.app/Contents/MacOS/Google\ Chrome \
     --user-data-dir=/tmp/opendia-bench-profile \
     --load-extension="$HOME/Dev/opendia/opendia-extension/dist/chrome" \
     --disable-extensions-except="$HOME/Dev/opendia/opendia-extension/dist/chrome" \
     --no-first-run --no-default-browser-check &
   ```

   Then start Everywhere (`Everywhere --mcp` or the GUI); the extension
   auto-connects on `ws://localhost:5555`.

5. Set env vars for the Claude CLI subprocess that both `run-ab.sh` and
   `run-ours.sh` spawn:

   ```bash
   export ANTHROPIC_BASE_URL='http://4.151.241.30:8787'
   export ANTHROPIC_AUTH_TOKEN='...'
   ```

## Per-fixture workflow

Each fixture lives at `bench/fixtures/<id>/`:

```
bench/fixtures/<id>/
├── task.md          # YAML front-matter + the user prompt
├── page/index.html  # the frozen static page replay-server.mjs serves
└── expected.json    # written by freeze-ab.sh after the 5× ab run
```

`task.md` front-matter (lint-enforced):

```
---
id: <id>
ci_tier: ci | manual
kind: static_html | har_replay
wait_for: <selector>     # required if our_tool = diff_snapshot
---
<task body — see §9 Rule 17, ci_tier=ci must reference only browser_* tools>
```

### Author + freeze

```bash
# 1. write the fixture
mkdir -p bench/fixtures/<id>/page
# create task.md and page/index.html

# 2. freeze ab baseline (5 runs + variance guard)
bash bench/runner/freeze-ab.sh <id>
# writes bench/fixtures/<id>/expected.json with tokens_median + tokens_runs

# 3. freeze ours
bash bench/runner/freeze-ours.sh <id>
# emits the "ours" half on stdout — pipe into bench/results/bench-results.json

# 4. judge
node bench/runner/judge.mjs <id>
# prints {correctness, judge_votes, token_ratio, pass}
```

If `freeze-ab` or `freeze-ours` returns variance > 20% after one rerun,
the row is marked `status=blocked reason=bench-variance-too-high` in
`docs/specs/parity-matrix.json` per SPEC §11.4 escape hatch.

## Preflight (avoiding silent failures)

`run-ours.sh` cannot detect by itself whether the OpenDia extension is
WS-attached to Everywhere — the only signal is that a tool call returns
`{ok:false, error:"opendia-not-connected"}`. Before running the bench
suite, sanity-check with:

```bash
echo '{"id":1,"jsonrpc":"2.0","method":"tools/list"}' | "$EVERYWHERE_BIN" --mcp \
  | jq '.result.tools | map(select(.name | startswith("browser_"))) | length'
# expected: > 50 once the extension is connected; 0 if not.
```
