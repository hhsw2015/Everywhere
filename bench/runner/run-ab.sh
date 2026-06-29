#!/usr/bin/env bash
# bench harness: run a fixture against the locked agent-browser baseline.
# spec §11.3 — Claude Code CLI subprocess, model claude-sonnet-4-6, temp 0,
# fresh agent session per fixture, ab side sees only ab tools.
#
# Output (stdout): single JSON line with {fixture, side, answer, tokens, ms}.
# Errors -> stderr, non-zero exit.
set -euo pipefail

usage() { echo "usage: $0 <fixture-id>" >&2; exit 2; }
[[ $# -eq 1 ]] || usage
FIXTURE="$1"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FX_DIR="$ROOT/bench/fixtures/$FIXTURE"
[[ -d "$FX_DIR" ]] || { echo "no such fixture: $FIXTURE" >&2; exit 3; }

: "${ANTHROPIC_AUTH_TOKEN:?ANTHROPIC_AUTH_TOKEN unset}"
: "${ANTHROPIC_BASE_URL:?ANTHROPIC_BASE_URL unset}"

AB_SHA="ed2e10598c9064aecfaeb7cf21b540684db4be2c"
AB_CLONE="${AB_CLONE:-/tmp/agent-browser}"
AB_BIN="$AB_CLONE/cli/target/release/agent-browser"
if [[ ! -d "$AB_CLONE/.git" ]]; then
  git clone https://github.com/vercel-labs/agent-browser.git "$AB_CLONE" >&2
fi
( cd "$AB_CLONE" && git fetch --quiet && git checkout --quiet "$AB_SHA" )
[[ "$(git -C "$AB_CLONE" rev-parse HEAD)" == "$AB_SHA" ]] || { echo "ab sha mismatch" >&2; exit 4; }
if [[ ! -x "$AB_BIN" ]]; then
  echo "ab binary not built; run: cd $AB_CLONE/cli && cargo build --release" >&2
  exit 5
fi

PORT="${BENCH_PORT:-7977}"
# Port collision = silent stale-fixture serving. Detect.
if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "preflight: port $PORT already in use; bench cannot start replay-server" >&2
  exit 7
fi
node "$ROOT/bench/runner/replay-server.mjs" --fixture "$FIXTURE" --port "$PORT" 2>/dev/null &
SRV=$!
trap 'kill $SRV 2>/dev/null || true' EXIT
# Active wait — Node startup can exceed 1s on a cold cache.
for i in 1 2 3 4 5 6 7 8 9 10; do
  if curl -sf "http://127.0.0.1:$PORT/" >/dev/null 2>&1; then break; fi
  sleep 0.5
done

# Strip front-matter, keep task body.
TASK_BODY="$(awk 'BEGIN{n=0} /^---$/{n++; next} n==2{print}' "$FX_DIR/task.md")"
SYSTEM="$(cat "$ROOT/bench/runner/system-prompt.md")"

# Compose MCP config: ab MCP stdio server, core profile (matches v0.31.1 default).
MCP_CFG="$(cat <<JSON
{"mcpServers":{"agent_browser":{"command":"$AB_BIN","args":["mcp","--tools","all"]}}}
JSON
)"

START_MS="$(python3 -c 'import time; print(int(time.time()*1000))')"

# claude -p emits JSON containing { result, total_cost_usd, usage:{...,input_tokens,output_tokens} }
OUT="$(claude -p \
  --bare \
  --model claude-sonnet-4-6 \
  --output-format json \
  --strict-mcp-config \
  --mcp-config "$MCP_CFG" \
  --system-prompt "$SYSTEM" \
  --allow-dangerously-skip-permissions \
  --max-turns 25 \
  --allowedTools "mcp__agent_browser" \
  -- "$TASK_BODY" 2>/tmp/run-ab-stderr.log)" || {
    echo "claude -p failed; see /tmp/run-ab-stderr.log" >&2
    tail -40 /tmp/run-ab-stderr.log >&2
    exit 6
  }

END_MS="$(python3 -c 'import time; print(int(time.time()*1000))')"

# Extract: result text + token totals.
ANSWER=$(jq -r '.result // .messages[-1].content[0].text // empty' <<<"$OUT")
# Token accounting: input + output only. cache_creation is excluded
# because Anthropic's prompt cache lifecycle (5-min TTL) flips one
# random run out of every five into a 600x cliff, killing variance
# even when the agent's actual work is identical. cache_read is
# always excluded (discounted re-read). Both sides use the same
# accounting for symmetry.
TOK=$(jq -r '
  ((.usage.input_tokens // 0) +
   (.usage.output_tokens // 0))
' <<<"$OUT")
[[ -z "$TOK" || "$TOK" == "null" ]] && TOK=0

jq -c -n \
  --arg fx "$FIXTURE" \
  --arg ans "$ANSWER" \
  --argjson tok "$TOK" \
  --argjson dms $((END_MS - START_MS)) \
  '{fixture: $fx, side: "ab", answer: $ans, tokens: $tok, duration_ms: $dms}'
