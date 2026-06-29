# BLOCKED — per-cap last error + push history + suggested next move

Auto-appended by the `/goal` loop. Status mirrors `parity-matrix.json`
`status=blocked` rows. Reviewer reads this when deciding whether to flip a
row in handoff.

---

## `agent_browser_read`

- **First blocked**: 2026-06-29
- **Reason code**: `bench-variance-too-high`
- **Push count**: 0 (no PR opened; freeze attempted locally)
- **Last error**: `(max-min)/median = 0.21 > 0.20` on both 5-run sets. ab
  v0.31.1 alternates between the cheap `read` tool path and the heavier
  `open → snapshot → get_text` path; token counts diverge by ~21%
  between runs even with `temperature 0` and identical system prompts —
  Claude's tool-pick non-determinism dominates.
- **Suggested next move**: tighten the fixture task body to demand a
  specific tool path (e.g. "use `agent_browser_read` to fetch the URL;
  do not snapshot"). This biases ab into a single code path and should
  collapse variance. Then re-attempt freeze.

  Alternative: switch to a `kind: har_replay` fixture pinning the
  network surface to one transaction so the agent's choice of code path
  no longer affects request count.
