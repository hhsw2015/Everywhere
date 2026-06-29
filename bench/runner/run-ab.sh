#!/usr/bin/env bash
# bench harness: run a fixture against the locked agent-browser baseline.
# spec §11.3 — Claude Code CLI subprocess, model claude-sonnet-4-6, temp 0.
# This script is invoked by Phase 0.5 (5 runs per fixture, baseline freeze)
# and by Phase 3 (re-record IS FORBIDDEN at gate time; see Lint Rule 15).
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
if [[ ! -d "$AB_CLONE/.git" ]]; then
  git clone https://github.com/vercel-labs/agent-browser.git "$AB_CLONE" >&2
fi
( cd "$AB_CLONE" && git fetch --quiet && git checkout --quiet "$AB_SHA" )
[[ "$(git -C "$AB_CLONE" rev-parse HEAD)" == "$AB_SHA" ]] || { echo "ab sha mismatch" >&2; exit 4; }

# Spin up the local replay server for the fixture's frozen page/.
PORT="${BENCH_PORT:-7977}"
node "$ROOT/bench/runner/replay-server.mjs" --fixture "$FIXTURE" --port "$PORT" &
SRV=$!
trap 'kill $SRV 2>/dev/null || true' EXIT
sleep 1

TASK_BODY="$(awk '/^---$/{n++; next} n==2{print}' "$FX_DIR/task.md")"
SYSTEM="$(cat "$ROOT/bench/runner/system-prompt.md")"

# Build ab MCP server command. claude-code CLI driver is left to the
# harness owner; this script ONLY emits a JSON line ready for the judge.
echo '{"fixture":"'"$FIXTURE"'","side":"ab","status":"stub","note":"baseline replay not yet wired (Phase 0.5 fills this)"}'
