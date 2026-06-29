#!/usr/bin/env bash
# bench harness: run a fixture against Everywhere (GUI HTTP MCP) +
# OpenDia extension. spec §11.3, §11.5.
#
# Fairness invariants enforced:
#   - allowedTools narrowed to mcp__everywhere__browser_* only (no
#     macOS-native tools that ab doesn't have); avoids 39 unrelated
#     tools polluting the comparison.
#   - port collision detected via lsof preflight (P0 fix).
#   - token total includes input + output + cache_creation
#     (cache_read excluded; that's a discounted re-read).
#   - replay-server readiness wait via curl probe loop (not blind sleep).
#   - browser session reset before each run via browser_state_clean
#     (prevents cookie/storage carryover between freeze runs).
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

EVERYWHERE_URL="${EVERYWHERE_URL:-http://127.0.0.1:7878/mcp}"
PORT="${BENCH_PORT:-7977}"

# ---- Preflight: Everywhere HTTP MCP up + extension connected ----
# Streamable HTTP transport: tools/list responds via SSE without
# requiring an explicit session id, so we just hit it directly.
TOOLS_JSON="$(curl -sS "$EVERYWHERE_URL" \
  -H 'content-type: application/json' \
  -H 'accept: application/json,text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' 2>/dev/null \
  | awk '/^data: /{print substr($0,7)}' | head -1 || true)"
if [[ -z "$TOOLS_JSON" ]]; then
  echo "preflight: Everywhere HTTP MCP not reachable at $EVERYWHERE_URL" >&2
  exit 4
fi
BROWSER_COUNT="$(jq '[.result.tools[].name | select(startswith("browser_"))] | length' <<<"$TOOLS_JSON" 2>/dev/null || echo 0)"
if [[ "${BROWSER_COUNT:-0}" -lt 50 ]]; then
  echo "preflight: only $BROWSER_COUNT browser_* tools — OpenDia extension is not connected to Everywhere on ws://localhost:5555" >&2
  echo "          load the extension into a Chromium profile (see bench/README.md)" >&2
  exit 5
fi

# ---- Port collision detection (P0 fix #5) ----
if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "preflight: port $PORT already in use; bench cannot start replay-server" >&2
  echo "          run \`lsof -nP -iTCP:$PORT -sTCP:LISTEN\` to find the offender" >&2
  exit 7
fi

# ---- Launch replay-server ----
node "$ROOT/bench/runner/replay-server.mjs" --fixture "$FIXTURE" --port "$PORT" 2>/dev/null &
SRV=$!
trap 'kill $SRV 2>/dev/null || true' EXIT

# Active wait until server responds (instead of blind sleep).
for i in 1 2 3 4 5 6 7 8 9 10; do
  if curl -sf "http://127.0.0.1:$PORT/" >/dev/null 2>&1; then break; fi
  sleep 0.5
done

# ---- Reset browser session (P0 fix #11) ----
# Wipe any leftover cookies on the fixture URL. We don't touch real
# domains — bench runs in a dedicated browser profile.
curl -sS "$EVERYWHERE_URL" \
  -H 'content-type: application/json' \
  -H 'accept: application/json,text/event-stream' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"browser_cookies_clear","arguments":{"url":"http://127.0.0.1:'"$PORT"'/"}}}' >/dev/null 2>&1 || true

# ---- Build the narrowed tool allowlist (P0 fix #3 + #4) ----
# Allow only browser_* and the umbrella mcp__everywhere prefix is too
# broad. Build a comma-separated list of mcp__everywhere__browser_*
# names from the live tool list.
ALLOWED="$(jq -r '[.result.tools[].name | select(startswith("browser_")) | "mcp__everywhere__" + .] | join(",")' <<<"$TOOLS_JSON")"
if [[ -z "$ALLOWED" ]]; then
  echo "preflight: failed to build allowed-tool list (BROWSER_COUNT=$BROWSER_COUNT)" >&2
  exit 8
fi

TASK_BODY="$(awk 'BEGIN{n=0} /^---$/{n++; next} n==2{print}' "$FX_DIR/task.md")"
SYSTEM="$(cat "$ROOT/bench/runner/system-prompt.md")"

MCP_CFG="$(cat <<JSON
{"mcpServers":{"everywhere":{"url":"$EVERYWHERE_URL","type":"http"}}}
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
  --allowedTools "$ALLOWED" \
  -- "$TASK_BODY" 2>/tmp/run-ours-stderr.log)" || {
    echo "claude -p failed; see /tmp/run-ours-stderr.log" >&2
    tail -40 /tmp/run-ours-stderr.log >&2
    exit 6
  }

END_MS="$(python3 -c 'import time; print(int(time.time()*1000))')"

ANSWER=$(jq -r '.result // .messages[-1].content[0].text // empty' <<<"$OUT")
# P0 fix #7: include cache_creation_input_tokens; cache_read is the
# discounted re-read so excluded.
TOK=$(jq -r '
  ((.usage.input_tokens // 0) +
   (.usage.output_tokens // 0) +
   (.usage.cache_creation_input_tokens // 0))
' <<<"$OUT")
[[ -z "$TOK" || "$TOK" == "null" ]] && TOK=0

jq -c -n \
  --arg fx "$FIXTURE" \
  --arg ans "$ANSWER" \
  --argjson tok "$TOK" \
  --argjson dms $((END_MS - START_MS)) \
  '{fixture: $fx, side: "ours", answer: $ans, tokens: $tok, duration_ms: $dms}'
