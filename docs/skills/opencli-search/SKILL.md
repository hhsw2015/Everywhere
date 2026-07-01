---
name: opencli-search
description: |
  Route real-world data queries through Everywhere's embedded OpenCLI
  adapters (hundreds of sites, updated per upstream refresh). Use when
  the user wants data from a specific site — Reddit thread, HN top
  stories, PyPI package info, 36kr news, bilibili trending, Zhihu
  answer, etc. — instead of guessing at raw HTTP or scraping.
  Discover via `opencli_list`, describe via `opencli_describe`,
  execute via `opencli_run`. When in doubt about which site has a
  command, call `opencli_list({query: "..."})`.
allowed-tools: mcp__everywhere-http__opencli_list, mcp__everywhere-http__opencli_describe, mcp__everywhere-http__opencli_run, mcp__everywhere-http__web_search, mcp__everywhere-http__web_fetch_url
---

# opencli-search

Adapted from upstream `jackwener/OpenCLI` `skills/smart-search`, retargeted
at Everywhere's embedded runtime (v0.9.278+). Same routing philosophy;
different execution surface — the tools are MCP calls, not shell commands.

## Core principle

**Don't hard-code sites or command signatures.** The registry has ~170
sites and shifts every upstream refresh. Discover from the live
runtime, run against the live runtime.

## Three tools, in order

```
opencli_list({})                       ← site index (170 rows, ~3 KB)
opencli_list({site:"reddit"})          ← every reddit command
opencli_list({query:"top stories"})    ← fuzzy across all sites (cap 60)
opencli_describe({site, name})         ← full schema for one command
opencli_run({site, name, arguments_json})  ← execute
```

`opencli_run`'s third arg is always a JSON **string** containing the
adapter args object. Empty args = `"{}"`. Arg keys use the exact
spelling from the adapter's manifest — often **kebab-case** (e.g.
`"post-id"`, `"max-length"`), never converted to camelCase. Get the
authoritative list from `opencli_describe`.

## Adapter strategies

Each command in the list carries a `strategy` and a `browser` flag.
This tells you what infrastructure is needed:

| strategy | browser | Needs | Runs on Everywhere? |
|----------|---------|-------|---------------------|
| `public` | `false` | Nothing — pure HTTP | ✅ Direct fetch through embedded runtime |
| `public` | `true` | OpenDia browser extension connected | ✅ if OpenDia is up |
| `cookie` | `true` | OpenDia connected + user logged into the site in that browser | ✅ if OpenDia is up + user signed in |
| `intercept` | `true` | Same as cookie + adapter captures a signed request | ✅ if OpenDia is up + user signed in |
| `ui` | `true` | Same as cookie + full DOM interaction | ✅ if OpenDia is up + user signed in |
| `local` | either | Local dev server / desktop CLI passthrough | ❌ Not in embedded runtime |

`cookie`/`intercept`/`ui` adapters ALSO require the user's browser tab
to be near the target origin — the runtime skips navigation if we're
already there, otherwise it moves the active tab. Warn the user before
firing one of these on a busy tab.

## Routing rules

**Rule 1 — user names a site**: call `opencli_list({site: ...})` to
see the site's commands, pick the closest fit, then run. Do not
skip the list step — command names and args shift each upstream sync.

**Rule 2 — user names a data type but no site**: fuzzy-search first.
- "read this HN post" → `opencli_list({query: "hackernews"})`
- "what's trending on GitHub" → `opencli_list({query: "github trending"})`

**Rule 3 — Chinese / regional content**: prefer 36kr, zhihu, weibo,
bilibili, juejin, toutiao, 12306 over English-first sites.

**Rule 4 — fall back to web_fetch_url / web_search**: only after ONE
of:
- `opencli_list({query})` returned zero matches; or
- The picked adapter failed with `BROWSER_NOT_READY` and the user
  isn't going to install OpenDia; or
- Two consecutive `opencli_run` calls returned `ok:false` with
  different codes (schema drift, transient upstream issue).
Do not fall back on the first `ok:false` — a re-run with corrected
args often succeeds.

## Trigger examples

- "帮我读一下这个 Reddit 帖子: <url>" → `opencli_list({site:"reddit"})` → pick `read` → `opencli_run("reddit","read","{\"post-id\":\"<url>\"}")`
- "hacker news 前 5 条" → `opencli_run("hackernews","top","{\"limit\":5}")`
- "pypi requests 包信息" → `opencli_run("pypi","package","{\"name\":\"requests\"}")`
- "36 氪最近新闻" → `opencli_run("36kr","news","{\"limit\":10}")`
- "OEIS 上找找斐波那契数列" → `opencli_run("oeis","search","{\"query\":\"fibonacci\"}")`
- "看看 crates.io 上 serde crate" → `opencli_run("crates","search","{\"query\":\"serde\"}")`

## Error-envelope conventions

Every `opencli_run` response is JSON with:

```json
{"schema_version":"1","ok":true|false,"site":"...","name":"...","data":...,"elapsed_ms":123}
```

On failure:

```json
{"ok":false,"error":"...","code":"..."}
```

Codes you'll encounter:
- `RUNTIME_NOT_FOUND` — no such adapter (rare after `opencli_list`)
- `BAD_ARGS` — `arguments_json` didn't parse as a JSON object
- `BROWSER_NOT_READY` — cookie/intercept/ui adapter but OpenDia isn't
  connected. Suggest user install / connect the OpenDia extension.
- `ADAPTER_LOAD_FAILED` — the `.js` file has a syntax error or missing
  dependency. Almost certainly an upstream regression to log to the
  parity-wide gate; don't retry.
- `RUNTIME_HOST_ERROR` — something unexpected on our host side (V8
  wedged, host object mismatch). Retry once, then report.
- `RUNTIME_SCRIPT_ERROR` — the adapter threw. See `error` for the
  adapter's own message.
- `RUNTIME_PIPELINE_ONLY` — extremely rare after v0.9.262 (upstream
  pipeline runner is vendored + wired). If you see it, either the
  installed build predates v0.9.262 or the adapter uses a pipeline
  step we couldn't vendor (e.g. `download` — needs yt-dlp/streams).
  Don't retry; either upgrade or route via a different adapter.

## Post-run summary (mandatory)

At the end of any user-facing answer that used OpenCLI, append:

```
搜索摘要
- opencli_run <site>/<name>: <status> · <shape>
```

For multi-hop calls, one line per hop. This makes upstream regressions
visible fast without needing to open logs.

## When NOT to use this skill

- User asks about local files / their machine → use `everywhere` skill instead.
- User wants to drive their browser / see rendered DOM → use `everywhere` skill's Browser Use family, not an OpenCLI browser adapter.
- User asks a question you can answer from your own knowledge → answer directly.

## References

- Full parity matrix (Node vs MCP, 56 adapters covered): `bench/opencli/results/parity-wide.json`.
- Regression gate script: `scripts/test-opencli-parity-wide.mjs`.
- SPEC: `docs/specs/everywhere-opencli-adapters.md`.
- Upstream skill this was adapted from: https://github.com/jackwener/OpenCLI/tree/main/skills/smart-search
