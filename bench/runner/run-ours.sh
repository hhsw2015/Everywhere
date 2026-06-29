#!/usr/bin/env bash
# bench harness: run a fixture against Everywhere + OpenDia (ours side).
# spec §11.3, §11.5 — same model and system prompt as run-ab.sh; restricts
# Claude to the browser-only subset of Everywhere's MCP surface so the
# token comparison is apples-to-apples with ab's `mcp__agent_browser`
# allowlist (ab has no macOS-side capabilities).
#
# Output (stdout): single JSON line {fixture, side:"ours", answer, tokens, duration_ms}.
set -euo pipefail

usage() { echo "usage: $0 <fixture-id>" >&2; exit 2; }
[[ $# -eq 1 ]] || usage
FIXTURE="$1"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FX_DIR="$ROOT/bench/fixtures/$FIXTURE"
[[ -d "$FX_DIR" ]] || { echo "no such fixture: $FIXTURE" >&2; exit 3; }

: "${ANTHROPIC_AUTH_TOKEN:?ANTHROPIC_AUTH_TOKEN unset}"
: "${ANTHROPIC_BASE_URL:?ANTHROPIC_BASE_URL unset}"

# Path to the Everywhere binary (built via `dotnet publish src/Everywhere.Mac`).
# Override with EVERYWHERE_BIN=/path/to/Everywhere when running locally.
EVERYWHERE_BIN="${EVERYWHERE_BIN:-$ROOT/src/Everywhere.Mac/bin/Release/net10.0/osx-arm64/publish/Everywhere}"
if [[ ! -x "$EVERYWHERE_BIN" ]]; then
  echo "Everywhere binary not built; expected at $EVERYWHERE_BIN" >&2
  echo "Build: cd $ROOT && dotnet publish src/Everywhere.Mac/Everywhere.Mac.csproj -c Release -r osx-arm64" >&2
  exit 5
fi

PORT="${BENCH_PORT:-7977}"
node "$ROOT/bench/runner/replay-server.mjs" --fixture "$FIXTURE" --port "$PORT" 2>/dev/null &
SRV=$!
trap 'kill $SRV 2>/dev/null || true' EXIT
sleep 1

TASK_BODY="$(awk 'BEGIN{n=0} /^---$/{n++; next} n==2{print}' "$FX_DIR/task.md")"
SYSTEM="$(cat "$ROOT/bench/runner/system-prompt.md")"

# Everywhere is the only MCP server Claude sees — opendia is reached via
# Everywhere's WS-bridge under the browser_* prefix. Limit Claude to that
# subset so the bench compares like-for-like with ab.
MCP_CFG="$(cat <<JSON
{"mcpServers":{"everywhere":{"command":"$EVERYWHERE_BIN","args":["--mcp"]}}}
JSON
)"

START_MS="$(python3 -c 'import time; print(int(time.time()*1000))')"

OUT="$(claude -p \
  --bare \
  --model claude-sonnet-4-6 \
  --output-format json \
  --strict-mcp-config \
  --mcp-config "$MCP_CFG" \
  --system-prompt "$SYSTEM" \
  --allow-dangerously-skip-permissions \
  --max-turns 25 \
  --allowedTools "mcp__everywhere" \
  -- "$TASK_BODY" 2>/tmp/run-ours-stderr.log)" || {
    echo "claude -p failed; see /tmp/run-ours-stderr.log" >&2
    tail -40 /tmp/run-ours-stderr.log >&2
    exit 6
  }

END_MS="$(python3 -c 'import time; print(int(time.time()*1000))')"

ANSWER=$(jq -r '.result // .messages[-1].content[0].text // empty' <<<"$OUT")
TOK=$(jq -r '
  (.usage.input_tokens // 0) + (.usage.output_tokens // 0)
  // (.total_tokens // 0)
' <<<"$OUT" | head -1)
[[ -z "$TOK" || "$TOK" == "null" ]] && TOK=0

jq -c -n \
  --arg fx "$FIXTURE" \
  --arg ans "$ANSWER" \
  --argjson tok "$TOK" \
  --argjson dms $((END_MS - START_MS)) \
  '{fixture: $fx, side: "ours", answer: $ans, tokens: $tok, duration_ms: $dms}'
