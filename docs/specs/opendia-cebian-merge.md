# OpenDia + Cebian merge — SPEC v1

**Status**: draft, ready for implementation.
**Depends on**: `everywhere-self-expanding.md` (v3, tag `v0.9.310`).

## Repo locations

| Role | Local path | Remote |
|------|------------|--------|
| **Baseline** — OpenDia extension (this SPEC modifies) | `/Users/wowdd1/Dev/opendia/opendia-extension/` | `github.com/Sylinko/opendia` (fork) |
| **Source library** — Cebian, code copied FROM here | `/tmp/Cebian/` (cloned this session) | `github.com/maotoumao/Cebian` |
| **Consumer** — Everywhere daemon (adds `chat_*` MCP tools) | `/Users/wowdd1/Dev/Everywhere/src/Everywhere.Mcp/` | this repo |
| **SPEC itself** | `/Users/wowdd1/Dev/Everywhere/docs/specs/opendia-cebian-merge.md` | this repo |

**License note**: OpenDia is MIT (`/Users/wowdd1/Dev/opendia/LICENSE`, `opendia-extension/package.json:20`). Cebian is AGPL-3.0. Verbatim copy in Phase 3 makes the merged extension **AGPL-derivative**. If the extension must stay MIT, run Phase 3 as a reimplement instead of a copy (+4-6 weeks). Everywhere daemon (.NET) is untouched, keeps its license.

---

## 0. Goal

**Fully merge Cebian into OpenDia's source tree.** One Chrome extension: brand + id remain OpenDia; UI is Cebian's sidepanel; the old popup is deleted; its contents become a Cebian Settings page. Zero-diff on the 164 tools OpenDia exposes to Everywhere daemon.

**Two-way chat bus**: sidepanel and daemon-side agents (Claude Code / Cursor via daemon MCP) share one conversation via new `chat_*` MCP tools.

---

## 1. Acceptance signals (M1..M10)

| # | Signal |
|---|--------|
| M1 | Extension id preserved (verified against current Chrome Web Store detail page). Chrome shows one "OpenDia" entry. |
| M2 | Everywhere daemon connects, sees all 164 pre-merge `browser_*` tools with **zero schema diff** vs Phase-0 snapshot. |
| M3 | Clicking the toolbar icon opens the sidepanel directly (no popup). Sidepanel renders Cebian's chat UI. |
| M4 | Cebian Settings → "MCP Bridge" page has feature parity with the old popup (daemon status, tool count matching M2, current tab, WebSocket URL, reconnect/disconnect). |
| M5 | Sending a message in the sidepanel triggers a real LLM call; response streams back. |
| M6 | The sidepanel AI can invoke any of the 164 `browser_*` tools AND all Everywhere daemon `capture_* / web_* / memory_* / adapter_*` tools. |
| M7 | Daemon `chat_read(chat_id)` returns messages the user just typed in the sidepanel. |
| M8 | Daemon `chat_send(chat_id, role='assistant', text)` from Claude Code appears in the sidepanel within 2 s. |
| M9 | Killing the daemon leaves the sidepanel chat + LLM + loopback tools functional. |
| M10 | Killing the extension leaves daemon's non-chat tools functional; `chat_*` return `EXTENSION_NOT_CONNECTED`. |

---

## 2. Invariants (HARD)

- **Non-regression**: every `browser_*` tool keeps its exact input schema and return shape. `chrome.debugger` attach + `_cdpAttached` state map unchanged. WebSocket transport frame format (`{id,method,params}` / `{id,result|error}`) unchanged. Everywhere daemon's `OpenDiaBridge.CallToolAsync(unprefixedName, args)` contract unchanged — names stay unprefixed on the wire, daemon adds `browser_` prefix on its MCP surface as today. All 168 existing Everywhere unit tests pass.
- **Extension id**: `manifest.json` `key` field preserved. Old storage keys under `chrome.storage.local` keep their names. Cebian-imported code MUST namespace new keys under `cebian:*`.
- **License**: default path is verbatim copy of Cebian → merged extension is AGPL-derivative. Repo owner must accept this or pick the reimplement path before Phase 3.
- **Kill switches**:
  - `OPENDIA_CHAT_UI=0` — hides sidepanel (rollback for Phase 2+)
  - `OPENDIA_LEGACY_POPUP=1` — restores `popup.html` and `action.default_popup` (emergency rollback for Phase 3)
  - `activate_domain("chat")` on daemon side — gates chat tools per SPEC self-expanding §Phase 6

---

## 3. Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  Chrome                                                      │
│   ┌────────────────────────────────────────────────────┐     │
│   │  OpenDia extension (id preserved, no popup)        │     │
│   │  Toolbar icon → chrome.sidePanel.open()            │     │
│   │                                                    │     │
│   │  ┌──────────────────┐   ┌──────────────────────┐   │     │
│   │  │  sidepanel       │   │  background SW       │   │     │
│   │  │  (Cebian UI)     │◄──┤  - 164 tool handlers │   │     │
│   │  │                  │mcp│  - Chat store        │   │     │
│   │  │  React chat      │loop  - MCP router:       │   │     │
│   │  │  MCP client      │   │    * WebSocket→daemon│   │     │
│   │  │  BYOK LLM        │   │    * Loopback→panel  │   │     │
│   │  │  Settings > MCP  │   │                      │   │     │
│   │  │    Bridge page   │   │                      │   │     │
│   │  └──────────────────┘   └──────────┬───────────┘   │     │
│   └──────────────────────────────────────┼─────────────┘     │
└──────────────────────────────────────────┼───────────────────┘
                                           │  WebSocket (frames unchanged)
                                           ▼
                    ┌──────────────────────────────────────┐
                    │  Everywhere daemon                   │
                    │  MCP server on http://127.0.0.1:7878 │
                    │  - existing self-expand tools        │
                    │  - NEW: chat_* MCP tools             │
                    │    (proxied to extension chat store) │
                    └──────────────┬───────────────────────┘
                                   │  MCP over HTTP
                        ┌──────────┴──────────┐
                        ▼                     ▼
                   Claude Code            Cursor / …
```

**Ownership**:
- **Extension** is the source of truth for chat state (`chrome.storage.local` under `cebian:chats/<chat_id>`).
- **Daemon** proxies chat reads/writes; caches only subscriber cursors, no durable chat data.
- **Same tool handlers** serve both transports (WebSocket + loopback MCP) — no CDP-attach duplication.

---

## 4. Phase plan

Six phases. Each leaves the tree buildable and pre-merge behavior intact.

### Phase 0 — Baseline (0.5 d, OpenDia repo)

Freeze current behavior as a test target so later phases have something to diff against.

- `tests/opendia/baseline-tool-schemas.json` — one entry per tool: input schema + one sample output shape produced against canned pages.
- `tests/opendia/baseline-ws-frames.jsonl` — captured WebSocket frames from a full `tools/list → capture_start → cdp_evaluate → capture_stop` cycle.
- `scripts/opendia-back-to-back.mjs` — runs an alternate build, diffs against the baseline. **Exit 0 = safe to ship.**

**Acceptance**: baseline committed; script exits 0 against unmodified current build.

### Phase 1 — WXT migration (3-5 d, OpenDia repo)

Repackage OpenDia as a WXT project. Zero behavioral change.

- `opendia-extension/wxt.config.ts` — replicates current `manifest.json` field-for-field (preserve `key`, permissions, host_permissions, matches).
- `entrypoints/background/index.ts` — transliterate current `background.js` to ESM. No logic change.
- `entrypoints/popup/` — current `popup.html/js` unchanged (deleted in Phase 3).
- `entrypoints/content/` — current content-scripts.
- `pnpm run build` produces `.output/chrome-mv3/` behaviorally identical to today's build.

**Guardrails**: no new `chrome.*` calls. WebSocket URL, message format, tool dispatch table unchanged. `chrome.debugger.attach` sites stay in the same functions.

**Acceptance**: WXT build installs; daemon lists 164 tools; back-to-back exits 0.

### Phase 2 — Sidepanel + loopback MCP scaffold (3-5 d, OpenDia repo)

Introduce a sidepanel + a second MCP transport into background. No LLM yet.

- `entrypoints/sidepanel/{index.html, main.tsx, App.tsx}` — minimal React shell.
- `lib/loopback-mcp/server.ts` — MCP server on `chrome.runtime.onConnect(port='mcp-loopback')`, sharing the WebSocket transport's handler table.
- `lib/loopback-mcp/transport.ts` — client-side transport adapter for `@modelcontextprotocol/sdk` speaking to a `chrome.runtime.Port`.
- Sidepanel calls `tools/list` via loopback → shows tool count as a debug badge.
- Manifest adds `side_panel: { default_path: 'sidepanel.html' }` and `permissions: ["sidePanel"]`.
- Popup **still available** in this phase (deleted in Phase 3). Sidepanel opened via a keyboard shortcut or a button inside the popup.
- Kill switch: `OPENDIA_CHAT_UI=0` disables sidepanel entry.

**Acceptance**: sidepanel opens; `daemon.tools/list` and `sidepanel.tools/list` return **byte-identical** JSON.

### Phase 3 — Cebian chat UI + popup removal (6-9 d, OpenDia repo)

Copy Cebian's chat surface into the extension. Delete the popup.

**Copy from `/tmp/Cebian/` verbatim (adjust import paths)**:
- `components/chat/*` → `entrypoints/sidepanel/components/chat/`
- `lib/agent/{system-prompt,message-helpers,attachments,compaction}.ts` → `lib/agent/`
- `lib/providers/*` (OpenAI / Anthropic / Google / OpenAI-compat) → `lib/providers/`
- `lib/mcp/{client,manager,throttle,rate-limiter}.ts` → merged with Phase 2's loopback wiring
- `components/settings/*` — Settings dialog scaffold

**Skip (v1)**: recorder, VFS (`lightning-fs`), Skills, Slash Prompts, memory system. **Also skip Cebian's `lib/browser/*` and `lib/tools/*`** — OpenDia's own handlers do that job via loopback MCP. This is the critical departure from Cebian's default wiring.

**BYOK LLM**: verified in this session — `@earendil-works/pi-ai@0.80.2` + `@earendil-works/pi-agent-core@0.80.2` install cleanly (`npm install` pulled 96 packages), ship ESM entrypoints, 30+ named exports (`createProvider`, `createAssistantMessageEventStream`, `InMemoryCredentialStore`, `envApiKeyAuth`, `calculateCost`, …). Build env requires Node ≥22 (warning on 20). Extension bundle runs in Chrome MV3 SW, not Node.

**Remove popup**:
- Delete `popup.html`, `popup.js` from source tree.
- Drop `action.default_popup` from manifest.
- Add background listener: `chrome.action.onClicked` → `chrome.sidePanel.open({tabId})`.
- Keep the popup's message handlers in background (`getStatus`, `getToolCount`, `getPorts`, `reconnect`, `disconnect`) — same wire format, new consumer.

**New Cebian Settings page: "MCP Bridge"** (~200-300 lines React):
- Daemon connection status pill (green/red)
- Advertised tool count (matches M2)
- Current active tab id + URL
- WebSocket server URL (editable)
- Connected MCP client count + names
- Reconnect / Disconnect / Manual-disconnect controls
- Advanced: log tail, last-seen error
- BYOK API keys stored under `cebian:providers/*` in `chrome.storage.local`.

**Acceptance**: M3, M4, M5, M6 pass. Sidepanel MCP list contains loopback (164) + `http://127.0.0.1:7878/mcp` (daemon's Phase 6 self-expand tools, tier-gated). At least one tool from each transport round-trips via a real LLM call.

### Phase 4 — Chat store + chat bus (2 d extension + 1.5 d daemon)

Make chat state a first-class shared resource.

**Extension** (OpenDia repo):

- `lib/chat/store.ts` — background singleton `ChatStore`, backed by `chrome.storage.local` under `cebian:chats/<chat_id>`.
- Schema:
  ```typescript
  {
    chat_id: string;              // uuid v4
    title: string;
    created_at: number;           // unix ms
    updated_at: number;
    msg_counter: number;          // monotonic per chat
    messages: Array<{
      msg_id: number;             // monotonic
      client_msg_id: string;      // uuid, idempotency key
      ts: number;
      role: 'user' | 'assistant' | 'tool';
      text: string;               // for role=tool: JSON-serialized result
      tool_call?: { name: string; args: unknown };
      metadata?: Record<string, unknown>;
    }>;
    origin: 'sidepanel' | 'daemon' | 'mixed';
    tab_hint?: number;
  }
  ```
- `append(chat_id, msg)` — idempotent on `client_msg_id`, assigns next `msg_id`, broadcasts `chat:appended` runtime event.
- New WebSocket handlers (frames go over the existing bridge):
  - `chat_list()` → `{ chats: [{chat_id, title, updated_at, message_count}] }`
  - `chat_read({chat_id, since_msg_id?, limit?})` → `{ chat_id, messages: [...] }`
  - `chat_send({chat_id, client_msg_id, role, text, tool_call?, metadata?})` → `{ msg_id, ts }`
  - `chat_create({title?, tab_hint?})` → `{ chat_id }`
  - `chat_delete({chat_id})` → `{ ok: true }`
  - `chat_subscribe({chat_id, sub_id, since_msg_id?})` → server-push frames `{type:'chat_appended', sub_id, chat_id, msg}` (§5)
  - `chat_unsubscribe({sub_id})`

**Daemon** (Everywhere repo):

- `src/Everywhere.Mcp/OpenDia/OpenDiaChatBus.cs` — wraps `OpenDiaBridge` for `chat_*` frames; per-subscriber `Channel<T>` for push events; auto-resubscribes with `since_msg_id=last_seen` after WebSocket reconnect.
- `src/Everywhere.Mcp/Tools/ChatBusTools.cs` — `[McpServerToolType]` with 6 MCP tools mirroring the WebSocket handlers, plus `chat_subscribe(chat_id, timeout_ms=30000)` as a long-poll (returns `{ok:true, timed_out:true}` on empty). All errors use canonical §5 envelope. `EXTENSION_NOT_CONNECTED` when WebSocket is down.
- Registered under `TierGate.Domains["chat"]` — hidden until `activate_domain("chat")`.

**Acceptance**: M7, M8. `chat_subscribe` from Claude Code blocks up to 30 s, wakes on real append. Kill-restart test: kill extension mid-subscribe, restart, verify caller sees messages that arrived during the gap.

### Phase 5 — Tool consolidation (2-4 d, optional v1, OpenDia repo)

For the four tools where Cebian's implementation is nicer than OpenDia's (`read_page`, `inspect`, `element-picker`, `interact`), add a feature-flagged swap.

- Implement Cebian's version behind the current OpenDia tool name.
- Flag: `OPENDIA_USE_CEBIAN_<TOOL>` in `chrome.storage.local`, default off in v1.
- `scripts/opendia-back-to-back.mjs` compares flag-off vs flag-on against canned pages, asserts structural equivalence.
- Flip default on for each tool only after 1 week of dogfood without regression.

**Acceptance**: both routes exercised in CI. Flag off = exact pre-merge behavior.

---

## 5. Wire formats

### 5.1 Chat WebSocket frames

Extension re-uses the existing WebSocket to daemon. Frames follow the pre-merge shape (`{id,method,params}` request → `{id,result|error}` reply). New `method` values: `chat_list`, `chat_read`, `chat_send`, `chat_create`, `chat_delete`, `chat_subscribe`, `chat_unsubscribe`.

Server-push (no `id`):
```json
{ "type": "chat_appended", "sub_id": "...", "chat_id": "...", "msg": { ... } }
{ "type": "chat_deleted",  "sub_id": "...", "chat_id": "..." }
```

**Subscribe / resume**:
- `chat_subscribe({chat_id, sub_id, since_msg_id?})`
  - If `since_msg_id` supplied and still known → server sends all messages after it, then keeps pushing new ones.
  - If unknown or omitted → fresh subscription, no backfill.
- Daemon caches `last_seen_msg_id` per `sub_id`, reissues subscribe with `since_msg_id=last_seen` on WebSocket reconnect. Reuses OpenDia's existing `reconnectAttempts` loop (`background.js:36` today).

### 5.2 Daemon-side MCP tool contracts

All 6 tools return canonical envelope on error (`{ok:false, code, message, details?}`).

Error codes added to `everywhere-self-expanding.md` §5:

| Code | Details |
|------|---------|
| `EXTENSION_NOT_CONNECTED` | `{}` |
| `CHAT_NOT_FOUND` | `{chat_id}` |
| `INVALID_ROLE` | `{provided, allowed:['user','assistant','tool']}` |
| `IDEMPOTENCY_CONFLICT` | `{client_msg_id}` — same id used with different content |

`chat_subscribe` timeout is not an error: returns `{ok:true, timed_out:true, messages:[], last_msg_id}`.

### 5.3 Chat_id format

uuid v4 lowercase hex, `^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`. Whichever side creates the chat first generates it.

---

## 6. Cross-store boundaries

- **Chat store ≠ CaptureSessionStore**: `chat_id` and `session_id` are disjoint namespaces. A `ChatSession` MAY reference a capture via `metadata.capture_session_id` (v2 optional auto-linkage: on first `capture_start` after a chat's first user message, record the session id). Daemon does not enforce.
- **Idempotency + ordering**: `chat_send` MUST include `client_msg_id` (uuid). Store rejects duplicates as no-op (returns existing `msg_id`). Order is by `msg_id`, not `ts`. `chat_read` and `chat_subscribe` return strictly `msg_id > since_msg_id`.
- **LLM keys**: BYOK API keys live in `chrome.storage.local` under `cebian:providers/*`. Daemon never reads or proxies them. Daemon has no `llm_call` MCP tool. Agents running through daemon (Claude Code, Cursor) use their own credentials.
- **Storage keys**: pre-merge OpenDia keys keep their names. All Cebian-imported keys namespaced under `cebian:*`.

---

## 7. Testing

**Extension side** (OpenDia repo):
- Phase 0 baselines are the primary contract. Every phase runs `scripts/opendia-back-to-back.mjs` in CI.
- Loopback MCP: unit test with mock `chrome.runtime.Port` proving one handler table is reachable from both transports.
- Chat store: unit test append/read/broadcast semantics with faked `chrome.storage.local`.

**Daemon side** (Everywhere repo, extends existing 168 tests):
- `ChatBusToolsTests.cs`:
  - `chat_list_no_extension_returns_EXTENSION_NOT_CONNECTED`
  - `chat_send_roundtrip_through_fake_bridge`
  - `chat_subscribe_returns_empty_ok_on_timeout`
  - `chat_subscribe_returns_message_when_bridge_pushes`
  - `chat_subscribe_resumes_after_reconnect_with_since_msg_id`
- Fake bridge extends the existing `IBrowserCallSink` (introduced in self-expanding Phase 2.5) to cover `chat_*` frames.

**End-to-end** (manual v1, Playwright v2): user types in sidepanel → external MCP client reads it within 2 s → external `chat_send` shows in sidepanel within 2 s.

---

## 8. Work distribution

| Phase | Person-days | Repo |
|-------|------------:|------|
| 0 Baselines | 0.5 | `/Users/wowdd1/Dev/opendia/opendia-extension/` |
| 1 WXT migration | 3-5 | ditto |
| 2 Sidepanel + loopback MCP | 3-5 | ditto |
| 3 Cebian import + popup removal + Settings page | 6-9 | ditto (copy from `/tmp/Cebian/`) |
| 4 Chat store + WS handlers (ext) | 2 | ditto |
| 4 `chat_*` MCP tools + tests (daemon) | 1.5 | `/Users/wowdd1/Dev/Everywhere/src/Everywhere.Mcp/` |
| 5 Tool consolidation (optional v1) | 2-4 | OpenDia repo |
| **Total** | **18-26** | ~75% OpenDia, ~10% Everywhere, ~15% integration |

Main work is on the plugin side.

---

## 9. Rollback

Each phase respects a kill switch:

| Phase | Rollback |
|-------|----------|
| 0 | Additive; nothing to roll back. |
| 1 | Revert to pre-migration build tag. Extension id preserved. |
| 2 | `OPENDIA_CHAT_UI=0` hides sidepanel entry. |
| 3 | `OPENDIA_CHAT_UI=0` hides chat UI. `OPENDIA_LEGACY_POPUP=1` restores `popup.html` and `action.default_popup`. Both default off; only build maintainers flip. |
| 4 | `chat` domain hidden until `activate_domain("chat")`. Users who don't activate never see it. |
| 5 | Per-tool feature flag, default off. Flip only after burn-in. |

---

## 10. Success

> Install OpenDia. Open the sidepanel. Type "read the top HN story and summarize it." The LLM calls `browser_get_url` (loopback MCP) → `browser_get_text` → responds; text streams into the sidepanel. Meanwhile a Claude Code window calls `chat_read` and sees the exact same conversation. Claude Code posts an assistant message via `chat_send`; it appears in the sidepanel within 2 seconds. The user sees "OpenDia" — never Cebian, never Everywhere daemon. Three components share one conversation truth.
