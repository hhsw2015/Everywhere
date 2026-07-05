---
name: saas-connect
description: |
  Call SaaS provider APIs (GitHub, OpenAI, Anthropic, Linear, Notion,
  Slack, Google, Airtable, Dropbox, Figma, HubSpot, ~829 providers /
  8154 actions) through Everywhere's embedded open-connector runtime.
  Use when the request touches a user's real SaaS account: "my GitHub
  issues", "create a Linear ticket", "send a Slack message", "read a
  Notion page". Credentials are configured out-of-band in the daemon's
  Web Console (http://127.0.0.1:7878/connector-ui/) — never asked from
  or shown to the agent.
allowed-tools: mcp__everywhere-http__connector_list, mcp__everywhere-http__connector_describe, mcp__everywhere-http__connector_run, mcp__everywhere-http__connector_list_connections
---

# saas-connect

Adapted from `docs/specs/everywhere-connector.md`.

## When to use

Signal: "my \<SaaS\>", account-scoped queries, or writes to a SaaS product.

Do NOT use for:
- Public pages → `web_fetch_url` / `web_search`
- Site scraping without auth → `opencli_*`
- Browser UI automation → browser tools

## Funnel

1. `connector_list` — no args → provider index; `service=X` → drill;
   `query=X` → fuzzy across all actions (cap 60). Prefer `query` when
   unsure which provider fits.
2. `connector_describe service name` — full input/output JSON schema.
   Read `required` fields before building `arguments_json`.
3. `connector_run service name arguments_json='{...}' [connection=X]` —
   execute. `arguments_json` matches the action's `inputSchema`. Pass
   `connection` for multi-account (e.g. `github` default vs
   `connection=work`).

## Envelope

```jsonc
{ "ok": true,  "data": {...}, "elapsed_ms": 1234 }
{ "ok": false, "code": "...", "error": "...", "hint": "..." }
```

`code`: `authorization_failed | rate_limited | invalid_input |
provider_error | RUNTIME_NOT_FOUND | RUNTIME_HOST_ERROR`.

## Credentials

- **Never** put an API key inside `arguments_json` — daemon injects it.
- If `code=authorization_failed`, tell the user to open
  http://127.0.0.1:7878/connector-ui/ and connect the provider.
  Do NOT ask for the token in chat.
- `connector_list_connections` shows what's configured (no secrets).

## Not covered

- Files > 25 MiB (transit cap)
- Providers requiring OAuth without a configured client (user must
  create an OAuth app first — Web Console guides them)
