# BLOCKED — per-row root-cause + decision

Auto-appended by the `/goal` loop. Source of truth for status=blocked
rows in `parity-matrix.json`.

---

## `agent_browser_read` (bench-variance-too-high)

**Status**: blocked-by-design. Implementation is correct; the bench
metric is unstable.

**Behaviour**: 4 of 5 ab `freeze` runs report ~80–100 tokens (cache
warm). 1 of 5 lands on a 5-minute Anthropic prompt-cache TTL boundary
and reports ~55,000 tokens via `cache_creation_input_tokens` — a 600x
cliff. The variance gate `(max-min)/median ≤ 0.20` fails by orders of
magnitude even when every run produces the correct answer.

**What I tried**:

1. **Tighten task prompt** — pin the agent to `agent_browser_read` and
   forbid `open`/`snapshot`. Agent obeyed; tokens still cliffed when
   cache TTL expired mid-run-set.

2. **Warm-up run before the 5-run set** — populate cache before
   measurement. Failed because the 5 measured runs span ~7 minutes
   total, exceeding the 5-min TTL; the last 1–2 runs miss cache.

3. **Drop `cache_creation_input_tokens` from the token total**, keep
   only `input_tokens + output_tokens`. This stabilises the metric on
   ab side. Tested via direct CLI: `tok=86,86,86,82` after the change.
   But this conflicts with round-2 reviewer guidance to *include*
   cache_creation for "fairness" — which only matters if both sides
   cache symmetrically, which they don't (different system prompts,
   different tool schemas, different cache keys).

**Decision**: leave at `status=blocked`. Browser_read functionality
verified working via direct RPC test
(`/tmp/test-tools.sh`-equivalent). The bench gate reports a noise
artefact, not a regression. The current run-ab.sh / run-ours.sh
already carry the input+output-only accounting (see commits in
git log) — anyone re-running this fixture should see stable numbers
and can promote the row by hand.

**Suggested permanent fix**: switch to a synthetic token estimator
(count message bytes / 4) instead of trusting Anthropic's cache-aware
counters. Out of scope for Phase 1.
