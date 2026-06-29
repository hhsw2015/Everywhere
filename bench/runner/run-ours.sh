#!/usr/bin/env bash
# bench harness: run a fixture against Everywhere + OpenDia (ours side).
# spec §11.3, §11.5 — same model and system prompt as run-ab.sh.
set -euo pipefail

usage() { echo "usage: $0 <fixture-id>" >&2; exit 2; }
[[ $# -eq 1 ]] || usage
FIXTURE="$1"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FX_DIR="$ROOT/bench/fixtures/$FIXTURE"
[[ -d "$FX_DIR" ]] || { echo "no such fixture: $FIXTURE" >&2; exit 3; }

: "${ANTHROPIC_AUTH_TOKEN:?ANTHROPIC_AUTH_TOKEN unset}"
: "${ANTHROPIC_BASE_URL:?ANTHROPIC_BASE_URL unset}"

PORT="${BENCH_PORT:-7977}"
node "$ROOT/bench/runner/replay-server.mjs" --fixture "$FIXTURE" --port "$PORT" &
SRV=$!
trap 'kill $SRV 2>/dev/null || true' EXIT
sleep 1

# In CI: launch headless Chromium with the opendia extension preloaded
# (see spec §11.5); locally: rely on the user's running OpenDia + Everywhere.
echo '{"fixture":"'"$FIXTURE"'","side":"ours","status":"stub","note":"Phase 3 wires the CLI subprocess + tool surface"}'
