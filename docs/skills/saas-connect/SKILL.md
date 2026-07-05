---
name: saas-connect
description: |
  Call SaaS provider APIs (GitHub, OpenAI, Anthropic, Linear, Notion,
  Slack, Dropbox, Google, Airtable, Figma, HubSpot, Cloudflare, ...
  ~829 providers, ~8154 actions) through Everywhere's embedded
  open-connector runtime. Use when the user asks about anything that
  lives in a real SaaS account rather than a public website: list my
  GitHub issues, create a Linear ticket, send a Slack message, look
  up a Notion page, read a Google Doc, search Airtable, etc.

  Credentials are configured out-of-band via the daemon's Web Console
  (http://127.0.0.1:7878/connector-ui/) — never asked from or shown to
  the agent. Discover actions via `connector_list`, describe them via
  `connector_describe`, execute via `connector_run`. Multiple accounts
  per provider are supported via the `connection` argument.
allowed-tools: mcp__everywhere-http__connector_list, mcp__everywhere-http__connector_describe, mcp__everywhere-http__connector_run, mcp__everywhere-http__connector_list_connections
---

# saas-connect

Adapted from `docs/specs/everywhere-connector.md` (v0.9.312+). Runs in
the daemon's dedicated V8 isolate — the vendored open-connector provider
tree lives under `3rd/open-connector/src/providers/`.

## When to use this skill

Use when the request touches **a user's real account inside a SaaS
product** — not a public page, and not a browser-driven task. Signals:

- "my GitHub / OpenAI / Notion / Slack / Linear / Google / …"
- account-scoped queries (my repos, my drafts, my starred items)
- write intents (create an issue, send a message, add a row)
- API-driven data (repo commit list, workspace metrics, API responses)

Do **not** use for:

- Public web pages (use `web_fetch_url` / `web_search`)
- Site-specific data with no auth (use `opencli_*` — Reddit thread,
  HN top, 36kr news, bilibili trending)
- Interactive UI automation (use browser-driven tools)

## Discovery → describe → run

Every task follows the same three-step funnel:

1. **`connector_list`** — figure out which provider + action fits.
   - No args → provider index (829 entries).
   - `service=<name>` → drill into one provider (86 GitHub actions,
     34 Linear actions, 15 OpenAI actions, ...).
   - `query=<text>` → fuzzy across every action name/description
     (capped at 60 hits). Prefer this when unsure which provider.
2. **`connector_describe`** — full JSON schema for the action's
   input + output. Read the `required` fields before constructing
   `arguments_json`.
3. **`connector_run`** — execute. Pass `arguments_json` as a JSON
   string matching the action's `inputSchema`. Pass `connection`
   when the user has multiple accounts on the same provider (e.g.
   `github` default vs `github` connection=`work`).

## Envelope shape

Every `connector_run` reply is:

```jsonc
// Success
{
  "schema_version": "1",
  "ok": true,
  "service": "github",
  "name": "get_current_user",
  "data": { /* provider response, arbitrary shape */ },
  "elapsed_ms": 1234.5
}

// Failure
{
  "schema_version": "1",
  "ok": false,
  "service": "github",
  "name": "get_current_user",
  "code": "authorization_failed" | "rate_limited" | "invalid_input" |
          "provider_error" | "RUNTIME_NOT_FOUND" | "RUNTIME_HOST_ERROR",
  "error": "…human readable…",
  "hint": "…(optional, e.g. env var name)…"
}
```

Never treat a missing `ok` field as success. Never surface `code` /
`error` verbatim to the user — translate to a plain sentence.

## Credential state

- Whether a provider is connected is visible via
  `connector_list_connections`. Non-empty = configured.
- If a call returns `code: authorization_failed`, tell the user to
  open **http://127.0.0.1:7878/connector-ui/** and connect the
  provider under the Providers tab. Do **not** ask them for the token
  in chat.
- Never pass a raw API key / OAuth token inside `arguments_json` —
  credentials are injected by the daemon; the action's `inputSchema`
  will not contain a credential field.

## Named connections

If the user says "my work GitHub" vs "my personal GitHub":

```
connector_run
  service=github
  name=list_my_repositories
  arguments_json='{"perPage":10}'
  connection=work        # ← optional; defaults to the primary connection
```

The connection name is stored as `service:name` in the daemon store
(see `docs/specs/everywhere-connector.md` §7). Reserved chars: colons
are rejected at the boundary.

## Common patterns

### List, then act

```
1. connector_list query="issue"           →  find github.list_repository_issues
2. connector_describe github list_repository_issues
3. connector_run github list_repository_issues
     arguments_json='{"owner":"hhsw2015","repo":"Everywhere","state":"open","perPage":10}'
```

### Ambiguous provider

```
1. connector_list query="send message"    → matches slack.chat_post_message,
                                             discord.create_message,
                                             telegram.send_message, ...
2. Ask the user which platform they meant, OR check
   connector_list_connections to see which they've configured.
```

### Multi-step

```
1. connector_run github get_repository owner=hhsw2015 repo=Everywhere
     → get repo id
2. connector_run github create_issue owner=hhsw2015 repo=Everywhere
     title="..." body="..."
```

## Cheat sheet — most-used providers

| Service      | Auth       | Notable actions |
|--------------|------------|------------------|
| github       | api_key    | get_current_user, list_my_repositories, list_repository_issues, create_issue, get_repository, search_repositories, list_commits, list_pull_requests |
| openai       | api_key    | list_models, chat_completions, embeddings |
| anthropic    | api_key    | messages, count_tokens |
| linear       | api_key    | issue_create, issues_list, team_list |
| notion       | oauth2     | search, retrieve_page, retrieve_database, query_database |
| slack        | oauth2     | chat_post_message, conversations_list, users_list |
| airtable     | api_key    | list_records, create_record, update_record |
| figma        | api_key/oauth2 | get_file, get_file_nodes, get_file_comments |
| gitlab       | api_key    | list_projects, get_project, list_issues |
| hackernews   | no_auth    | get_top_stories, get_item, search_posts |

## Not covered by this skill

- Anything requiring browser DOM (login walls, JS-rendered pages) →
  use browser-driven tools.
- Site-scraping without auth → use `opencli_*` skill.
- File uploads / downloads that exceed 25 MiB → transit store cap.
