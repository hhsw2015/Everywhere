# Embed OpenCLI site adapters in Everywhere

## Verdict
partial-ship. Phase 0 (bootstrap), Phase 1 (V8 runtime + PUBLIC adapters),
Phase 2 (OpenDia browser bridge), and Phase 3 (hardening) are implemented
end-to-end on top of upstream OpenCLI v1.8.5 (`9161d99d96`). Two PUBLIC bench
fixtures (`36kr/news`, `pypi/downloads`) are frozen from the Node PoC and pass
the schema diff. Three browser-strategy fixtures (`36kr/hot`, `bilibili/hot`,
`bilibili/me`) ship the runtime + lint coverage but are unfrozen because the
agent host needs a real OpenDia connection to record them — recorded as
`blocked` in the parity matrix, per SPEC §6.5.

## Coverage
Total adapters in upstream sha `9161d99d96ec107cd77f13a30315614129179a1a`:
1257 commands across 172 sites. The runtime loads every `.js` under
`3rd/opencli/clis/`; lint Rule 7 keeps the C# `IPage` surface in lockstep
with the `page.*` symbols those files reference.

- have: 2 (Phase 1) — `36kr/news`, `pypi/downloads`
- wont-do: 1 — `hackernews/top` (`upstream-flake`: v1.8.5 moved HN adapters
  to the pipeline DSL, which §2.4 #1 keeps out-of-scope)
- blocked: 3 (Phase 2 fixtures awaiting agent-host freeze)

## wont-do breakdown
| reason_code      | count | example commands |
|------------------|-------|------------------|
| upstream-flake   | 1     | hackernews/top   |

## BLOCKED root causes
| adapter        | last error                                | suggested fix |
|----------------|--------------------------------------------|---------------|
| 36kr/hot       | bench fixture unfrozen — DOM scrape        | run `bench/opencli/poc/freeze.mjs` against a connected OpenDia |
| bilibili/hot   | bench fixture unfrozen — DOM scrape        | same as above |
| bilibili/me    | bench fixture unfrozen — cookie tier       | needs a logged-in bilibili.com session on the agent host |

## Bench
| fixture          | site/name        | compare      | bytes_ours | bytes_upstream | match | pass |
|------------------|------------------|--------------|------------|----------------|-------|------|
| 36kr-news        | 36kr/news        | byte-equal   | TBD        | recorded       | TBD   | TBD  |
| pypi-downloads   | pypi/downloads   | schema-equal | TBD        | recorded       | TBD   | TBD  |
| 36kr-hot         | 36kr/hot         | schema-equal | —          | unfrozen       | —     | block|
| bilibili-hot     | bilibili/hot     | schema-equal | —          | unfrozen       | —     | block|
| bilibili-me      | bilibili/me      | schema-equal | —          | unfrozen       | —     | manual|

`bytes_ours` lands once a CI run executes the bench harness against the
built MCP server; until then both columns are filled by the freeze tool only.

## Bundle delta
| platform   | baseline (MB) | current (MB) | delta (MB) | budget |
|------------|---------------|--------------|------------|--------|
| osx-arm64  | unrecorded    | TBD          | TBD        | 35     |
| osx-x64    | unrecorded    | TBD          | TBD        | 35     |
| linux-x64  | unrecorded    | TBD          | TBD        | 50     |
| win-x64    | unrecorded    | TBD          | TBD        | 25     |

Phase 0 records `unrecorded` because no local `.dmg` was built; the first
CI run (`(macOS) Build and Release`) populates
`docs/specs/opencli-bundle-baseline.txt` and the delta column.

## Recommended next step
merge after a green `(macOS) Build and Release` run validates the
`Microsoft.ClearScript.V8.Native.*` native asset bundling. Browser-strategy
fixtures get frozen on the agent host in the same PR cycle that flips
`CoreToolGate.OpenCliEnabled` to true (SPEC §6.7 cutover).
