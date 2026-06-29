#!/usr/bin/env bash
# Phase 0.5: freeze ab baseline for one fixture (5 runs).
# spec §11.4: tokens_median = drop min+max, median of remaining 3.
# Variance guard: if (max-min)/median > 0.20, re-run once.
set -euo pipefail

usage() { echo "usage: $0 <fixture-id>" >&2; exit 2; }
[[ $# -eq 1 ]] || usage
FIXTURE="$1"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FX_DIR="$ROOT/bench/fixtures/$FIXTURE"
EXPECTED="$FX_DIR/expected.json"

AB_SHA="ed2e10598c9064aecfaeb7cf21b540684db4be2c"
N=5

run_set() {
  local toks=() answers=()
  for i in $(seq 1 $N); do
    echo "  run $i/$N..." >&2
    # Wait for port 7977 to clear from prior run before starting next.
    for j in 1 2 3 4 5 6 7 8 9 10; do
      if ! lsof -nP -iTCP:7977 -sTCP:LISTEN >/dev/null 2>&1; then break; fi
      sleep 0.5
    done
    local out=""
    # Retry once on transient API failure (rate limit, network blip).
    for attempt in 1 2; do
      if out="$(timeout 300 bash "$ROOT/bench/runner/run-ab.sh" "$FIXTURE")"; then
        break
      fi
      out=""
      [[ $attempt -lt 2 ]] && { echo "  run $i attempt $attempt failed; retrying..." >&2; sleep 5; }
    done
    [[ -n "$out" ]] || { echo "  run $i failed twice" >&2; return 1; }
    toks+=("$(jq -r '.tokens' <<<"$out")")
    answers+=("$(jq -r '.answer' <<<"$out")")
  done
  jq -c -n --argjson t "$(printf '%s\n' "${toks[@]}" | jq -s .)" \
           --argjson a "$(printf '%s\n' "${answers[@]}" | jq -R . | jq -s .)" \
    '{tokens_runs: $t, answers: $a}'
}

variance_ok() {
  local runs="$1"
  local min max median
  min=$(jq '[.tokens_runs[]] | min' <<<"$runs")
  max=$(jq '[.tokens_runs[]] | max' <<<"$runs")
  median=$(jq '[.tokens_runs[]] | sort | (.[length/2 | floor])' <<<"$runs")
  if [[ "$median" -eq 0 ]]; then return 1; fi
  python3 -c "
m=$median; lo=$min; hi=$max
print('ok' if (hi-lo)/m <= 0.20 else 'fail')
"
}

set1="$(run_set)"
v1="$(variance_ok "$set1")"
if [[ "$v1" != "ok" ]]; then
  echo "variance too high on first 5-run set; re-running once (spec §11.4)" >&2
  set1="$(run_set)"
  v2="$(variance_ok "$set1")"
  if [[ "$v2" != "ok" ]]; then
    echo "variance failed twice; BLOCKED" >&2
    exit 7
  fi
fi

# Final answer: most common.
ANSWER=$(jq -r '.answers | group_by(.) | max_by(length) | .[0]' <<<"$set1")
TOKENS_MEDIAN=$(jq '
  .tokens_runs
  | sort
  | (.[1:-1])
  | (sort | (.[length/2 | floor]))
' <<<"$set1")
TOKENS_RUNS=$(jq '.tokens_runs' <<<"$set1")

jq -n \
  --arg ans "$ANSWER" \
  --argjson tr "$TOKENS_RUNS" \
  --argjson tm "$TOKENS_MEDIAN" \
  --arg sha "$AB_SHA" \
  --arg ts "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  '{answer:$ans, tokens_runs:$tr, tokens_median:$tm, ab_sha:$sha, frozen_at:$ts}' \
  > "$EXPECTED"
echo "froze $EXPECTED (median=$TOKENS_MEDIAN, answer=\"$ANSWER\")" >&2
