#!/usr/bin/env bash
# SPEC §11.3 — invoke Everywhere's opencli_run for one fixture.
# Usage: bash run-everywhere.sh <fixture-id>
# Reads bench/opencli/fixtures/<id>/{meta,args}.json and POSTs to MCP.

set -euo pipefail
FIX="${1:?fixture id required}"
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
META="$ROOT/bench/opencli/fixtures/$FIX/meta.json"
ARGS="$ROOT/bench/opencli/fixtures/$FIX/args.json"
[ -f "$META" ] || { echo "no meta.json for $FIX" >&2; exit 1; }
[ -f "$ARGS" ] || { echo "no args.json for $FIX" >&2; exit 1; }

SITE=$(jq -r .site "$META")
NAME=$(jq -r .name "$META")
ARGS_JSON=$(jq -c . "$ARGS")
MCP="${EVERYWHERE_MCP:-http://127.0.0.1:7878/mcp}"

REQ=$(jq -nc \
  --arg site "$SITE" --arg name "$NAME" --arg args "$ARGS_JSON" \
  '{jsonrpc:"2.0",id:1,method:"tools/call",params:{name:"opencli_run",arguments:{site:$site,name:$name,arguments_json:$args}}}')

curl -s "$MCP" \
  -H 'content-type: application/json' \
  -H 'accept: application/json, text/event-stream' \
  -X POST -d "$REQ" \
  | tail -n +1 \
  | (grep -oE '\{.*\}' | tail -1)
