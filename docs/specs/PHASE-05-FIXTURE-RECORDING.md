# Phase 0.5 — Fixture recording procedure

Purpose: break the Phase 0.5 ↔ Phase 1 circular dependency. Phase 1's `capture_*`
tools eventually produce these fixtures; the manual ones let Phase 1 tests run
before the tools exist.

Two rounds:

## 0.5.1 Hand-crafted minimals (this commit)

- `tests/Everywhere.Mcp.Tests/fixtures/observation/hackernews-manual.json`
- `tests/Everywhere.Mcp.Tests/fixtures/observation/recaptcha-demo-manual.json`

Rules — **the schema is the spec §Phase 1 CaptureSession**, no deviation:

- `session_id`: uuid v4 literal (not `capture_session_id`)
- `origin`: top-frame hostname captured at `capture_start`
- `network.requests[].initiator_stack`: `{url, function, line, col}` — CDP shape
  already transformed
- `network.bodies_by_hash`: only include bodies actually consumed by tests;
  each ≤512KB
- `dom_mutations[].detail`: discriminated union — see spec §Phase 1
- All secrets pre-redacted per spec §Phase 1 Redactor. Grep against `Set-Cookie`,
  `Authorization`, `ghp_`, `sk_live_`, `xox[bapr]-`, `eyJ` must return zero raw
  hits before commit

Coverage the two files must give Phase 1 + Phase 2 tests:

- HN: mix of noise (analytics, css, vote) + likely_data (algolia search JSON,
  karma JSON) — used by verdict scorer smoke tests
- reCAPTCHA: two mutations (`.g-recaptcha` div, iframe with `recaptcha`
  title/src) — feeds CaptchaDetector fixtures

## 0.5.2 Full-fidelity recordings (after Phase 1 lands)

Once `capture_start` / `capture_stop` ship, record these live with the browser:

| File | Site | Purpose |
|------|------|---------|
| `booking-capture.json` | booking.com search results | E2 verdict accuracy (17 XHRs → ≤4 likely_data) |
| `twitter-capture.json` | twitter/x.com | Phase 2 signature scheme, JS index |
| `reddit-capture.json` | reddit.com thread | Phase 2 sourcemap resolve |
| `github-repo-capture.json` | github.com repo view | Phase 2 techstack detect |

Procedure per fixture:

1. `EVERYWHERE_MCP_SELFEXPAND=1` in server env
2. `capture_start(tab_id)` → keep `session_id`
3. Perform the workflow described in each row above (search, click, scroll)
4. `capture_stop(session_id)`
5. `capture_export(session_id)` → file at `~/.everywhere/captures/<uuid>.json`
6. Copy that file to `tests/Everywhere.Mcp.Tests/fixtures/observation/<row>.json`
7. Grep-audit (same list as 0.5.1) — zero raw secret hits before commit
8. Trim `bodies_by_hash` to only entries referenced by tests

Tests referencing 0.5.2 fixtures use `[Skip("phase-0.5.2-pending")]` until the
file lands. That skip disappears in the commit that adds the fixture.

Do NOT hand-edit 0.5.2 fixtures after recording (except redactor pass + body
trim). If schema changes, re-record.
